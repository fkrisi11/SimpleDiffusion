using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using System.Text.Json;
using SimpleDiffusion.Infrastructure;

namespace SimpleDiffusion.Components
{
    public partial class ResultsGallery
    {
        // Long-edge size for the result-grid thumbnails (tiles are ~400px; this covers retina).
        private const int GridThumbWidth = 768;

        [Inject] private IDialogService DialogService { get; set; }
        [Inject] private IJSRuntime JS { get; set; }
        [Inject] private HttpClient Http { get; set; }      // app's own client (for /gallery/*)
        [Inject] private ISnackbar Snackbar { get; set; }
        [Inject] private WildcardService Wildcards { get; set; }
        [Inject] private UiPreferences Prefs { get; set; }   // per-device "Gallery image optimization" toggle
        [Inject] private HistoryService History { get; set; } = default!;
        [Inject] private IConfiguration Configuration { get; set; } = default!; // NeverOOM flags
        private readonly Random _rng = new();

        private async Task SaveToGallery(int index)
        {
            if (index < 0 || index >= _finalImages.Count) return;
            var b64 = await GalleryServer.GetResultBase64Async(_finalImages[index]);
            if (b64 is not null) await GallerySaveFlow.RunAsync(DialogService, Http, Snackbar, b64);
        }

        private async Task ClearResults()
        {
            var ok = await DialogService.ShowMessageBox(
                "Clear results?",
                "Remove all generated images shown here?",
                yesText: "Clear", cancelText: "Cancel");
            if (ok != true) return;
            _finalImages.Clear();
            _captions.Clear();
            _pngPrompts.Clear();
            StateHasChanged();
        }

        /// <summary>Reset the gallery to a single source image (raw base64). Used before a
        /// "Make variations" run with keepExisting=true so the original stays alongside its variants.</summary>
        public void StartVariationGallery(string? originalBase64)
        {
            _finalImages.Clear();
            _captions.Clear();
            _pngPrompts.Clear();
            if (!string.IsNullOrEmpty(originalBase64))
            {
                try { var id = GalleryServer.SaveResult(originalBase64); _finalImages.Add(id); _ = GalleryServer.WarmResultTierAsync(id, GridThumbWidth); }
                catch { }
            }
            StateHasChanged();
        }

        public bool _isGenerating = false;
        bool _batchActive = false; // true while a batch is running and hasn't produced its result yet
        int _progress = 0;
        double _eta = 0; // seconds remaining (from the SD API)
        int _batchNumber = 0;
        int _expectedTotal = 0; // how many images this run will produce — drives the skeleton placeholders
        int _producedThisRun = 0; // images actually finished this run (avoids a phantom trailing skeleton)
        bool _keepExisting; // variations mode: the source image stays and results append after it

        // Tiles already on screen that count toward this run's expected output. In the normal flow the
        // previous run's images stand in as placeholders until the first preview replaces them, so they
        // shouldn't ALSO get skeletons stacked under them (that left an empty tile of dead space). In
        // keepExisting (variations) mode the source image is separate, so only count produced images.
        private int SkeletonCount =>
            Math.Max(0, _expectedTotal - (_keepExisting ? _producedThisRun : _finalImages.Count) - (string.IsNullOrEmpty(_currentPreview) ? 0 : 1));
        private List<string> _finalImages = new List<string>();
        private string _currentPreview = "";
        private Dictionary<string, object> _parsedMetadata = new();

        /// <param name="seedOverride">If set, the batch uses this seed instead of the one in the
        /// request — without mutating the UI's seed field (used by "Surprise Me").</param>
        /// <param name="keepExisting">If true, new results are appended to the gallery instead of
        /// replacing it (used by "Make variations" to keep the source image visible).</param>
        /// <param name="promptOverride">If set, the batch uses this positive prompt instead of the
        /// one in the request — without touching the UI's prompt field (used by "Surprise Me").</param>
        /// <param name="negativeOverride">Same as promptOverride, for the negative prompt.</param>
        public async Task GenerateImageWithProgress(long? seedOverride = null, bool keepExisting = false, string? promptOverride = null, string? negativeOverride = null)
        {
            _isGenerating = true;
            _batchActive = true;
            _isCancelling = false;
            _captionLoading.Clear();
            _pngPrompts.Clear(); // drop stale "add prompt" data from the previous run
            try { await JS.InvokeVoidAsync("sdHaptic.buzz", 30); } catch { }

            // Keep the previous images on screen until the new run actually has something to
            // show. They're cleared on the first real preview (or first result if previews are
            // off), so clicking Generate no longer instantly wipes the gallery.
            // When keepExisting is set we never clear, so a seeded image (e.g. the variation source)
            // stays and results append after it.
            bool clearedThisRun = keepExisting;

            // Notify owner to refresh ui
            await GenerationEnded.InvokeAsync(true);

            // Capture the intended count so the UI sliders don't change mid-run. Each batch yields
            // batch_size images, so the skeleton count must multiply by it (a single batch can still
            // produce several images).
            int totalBatches = _request.n_iter;
            _expectedTotal = totalBatches * Math.Max(1, _request.batch_size); // a skeleton tile per upcoming image
            _producedThisRun = 0;
            _keepExisting = keepExisting;
            var runIds = new List<string>(); // this run's result ids, recorded to history when it finishes

            // Paint the skeleton placeholders now, before the first (blocking) request — otherwise the
            // only render before a preview arrives is the progress poll, which often already carries a
            // preview, so the skeletons never get a frame on screen.
            _currentPreview = "";
            await InvokeAsync(StateHasChanged);

            // Create a temporary request for the loop to avoid UI binding issues
            var localRequest = _request.Clone();
            localRequest.n_iter = 1;
            if (seedOverride.HasValue) localRequest.seed = seedOverride.Value;

            // Don't send unnecessary data
            if (!localRequest.enable_refiner)
            {
                localRequest.refiner_checkpoint = null;
            }

            for (int i = 0; i < totalBatches; i++)
            {
                if (_isCancelling)
                    break;

                _batchNumber = i + 1;
                _batchActive = true;
                _progress = 0;
                _currentPreview = "";
                CancellationTokenSource cts = new();
                _progressCts = cts; // so Skip/Cancel can stop the poll the instant they're pressed

                var progressTask = Task.Run(async () => {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        var prog = await _httpClient.GetFromJsonAsync<SdProgressResponse>("sdapi/v1/progress", cts.Token);

                        if (prog != null)
                        {
                            _progress = (int)(prog.progress * 100);
                            _eta = prog.eta_relative;
                            var candidate = prog.current_image;

                            // Downscale + blank-check the preview here on the polling background thread,
                            // so the client receives a small JPEG (not the full-res base64) each tick.
                            string? small = null; bool blank = false;
                            if (!string.IsNullOrEmpty(candidate))
                                (blank, small) = GalleryServer.PreparePreview(candidate, GridThumbWidth, Prefs.OptimizeLargeImages);

                            await InvokeAsync(() =>
                            {
                                // Only replace the displayed image once the preview has real (non-black)
                                // data; until then keep the previously finished image instead of a black frame.
                                if (!blank && small != null && !cts.IsCancellationRequested)
                                {
                                    _currentPreview = small;

                                    // First real preview of this run: replace the old gallery now.
                                    if (!clearedThisRun)
                                    {
                                        _finalImages.Clear();
                                        _captions.Clear();
                                        clearedThisRun = true;
                                    }
                                }
                                StateHasChanged();
                            });
                        }
                        await Task.Delay(1000, cts.Token); // 1s cadence: half the preview allocations/traffic of 500ms
                    }
                });

                try
                {
                    // Resolve dynamic-prompt syntax ({a|b}, __wildcard__) fresh per image so a batch varies.
                    localRequest.prompt = DynamicPrompts.Resolve(promptOverride ?? _request.prompt, _rng, Wildcards.Get);
                    localRequest.negative_prompt = DynamicPrompts.Resolve(negativeOverride ?? _request.negative_prompt, _rng, Wildcards.Get);

                    // Apply NeverOOM (reForge memory-safety) per send, merged with any ControlNet scripts.
                    localRequest.alwayson_scripts = NeverOom.Merge(localRequest.alwayson_scripts,
                        Configuration.Flag("NeverOomUnet", false), Configuration.Flag("NeverOomVae", false),
                        NeverOom.EncoderTile(Configuration), NeverOom.DecoderTile(Configuration));

                    var response = await _httpClient.PostAsJsonAsync("sdapi/v1/txt2img", localRequest);
                    var result = await response.Content.ReadFromJsonAsync<Txt2ImgResponse>();

                    if (result?.images != null)
                    {
                        // If no live preview ever arrived, clear the old gallery now — right
                        // before the first new result is shown.
                        if (!clearedThisRun)
                        {
                            _finalImages.Clear();
                            _captions.Clear();
                            clearedThisRun = true;
                        }

                        // Persist outputs to the result store off-thread; keep only ids (#5). Pre-warm
                        // grid thumbnails so the tiles aren't blank while they lazily generate.
                        var ids = await Task.Run(() => GalleryServer.SaveResults(result.images));
                        _finalImages.AddRange(ids);
                        runIds.AddRange(ids);
                        _producedThisRun += ids.Count;
                        foreach (var id in ids) _ = GalleryServer.WarmResultTierAsync(id, GridThumbWidth);

                        // The finished image is now in the gallery — drop the in-progress preview
                        // and mark the batch done so neither the preview card nor the "starting"
                        // progress bar flashes while the loop finishes up.
                        _currentPreview = "";
                        _batchActive = false;

                        if (result.info != null)
                        {
                            ParseGenerationInfo(result.info);
                        }

                        await InvokeAsync(StateHasChanged);
                    }
                }
                catch (Exception ex)
                {
                    // A failed batch used to vanish silently — surface it so Generate isn't a no-op.
                    // (Suppress when the user cancelled/skipped, which is an expected interruption.)
                    if (!_isCancelling)
                        Snackbar.Add($"Generation failed: {ex.Message}", Severity.Error);
                }
                finally
                {
                    cts.Cancel();
                    try { await progressTask; } catch { }
                }
            }

            if (_finalImages.Any() && _tagService != null && _request != null)
            {
                _tagService.IncrementPromptTags(_request.prompt);
            }

            // Record this run to the per-device history so a refresh/crash can recover it.
            if (runIds.Count > 0 && _request != null)
                await History.AddAsync(HistoryEntry.FromTxt2Img(_request, runIds));

            _isGenerating = false;
            _batchActive = false;
            _isCancelling = false;
            _currentPreview = "";
            _expectedTotal = 0;
            await InvokeAsync(StateHasChanged);

            // Notify owner to refresh ui
            await GenerationEnded.InvokeAsync(true);
        }

        private void OpenLightbox(int index)
            => ImageLightbox.ShowAsync(DialogService, _finalImages.Select(id => $"/results/file?id={id}").ToList(), index);

        private async Task DownloadAllAsZip()
        {
            if (!_finalImages.Any()) return;

            // Pull each result's bytes from the store only now (on explicit download).
            var stamp = Stamp.File();
            var files = new List<(string, string)>();
            for (int i = 0; i < _finalImages.Count; i++)
            {
                var b64 = await GalleryServer.GetResultBase64Async(_finalImages[i]);
                if (b64 is not null) files.Add(($"SD_{stamp}_{i + 1}.png", b64));
            }
            if (files.Count == 0) return;
            var zipName = $"SD_Batch_{stamp}.zip";
            await JS.InvokeVoidAsync("downloadUrl", GalleryServer.BuildZipDownload(files), zipName);
        }

        private void ParseGenerationInfo(string jsonInfo)
        {
            try
            {
                // Deserialize the JSON string into a dictionary for easy UI binding
                _parsedMetadata = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonInfo)
                                  ?? new Dictionary<string, object>();
            }
            catch
            {
                _parsedMetadata = new Dictionary<string, object>();
            }
        }

        private bool _isCancelling = false;
        private CancellationTokenSource? _progressCts; // the active progress poll, so Cancel/Skip can stop it now

        private async Task Interrupt()
        {
            _isCancelling = true;
            _progressCts?.Cancel(); // stop polling + preview churn immediately; the SD step still has to finish
            StateHasChanged();      // show the "Stopping…" state right away instead of waiting for the request
            // This tells the SD Backend to stop everything (it acts at the next sampling step).
            try { await _httpClient.PostAsync("sdapi/v1/interrupt", null); } catch { }
        }

        private async Task Skip()
        {
            // This tells the SD Backend to stop the current image and move to the next.
            try { await _httpClient.PostAsync("sdapi/v1/skip", null); } catch { }
        }

        public class InterrogateRequest
        {
            public string image { get; set; } = "";
            public string model { get; set; } = "clip"; // "clip" or "deepdanbooru" (if available)
        }

        public class InterrogateResponse
        {
            public string caption { get; set; } = "";
        }

        // Captions per image (index -> caption)
        public readonly Dictionary<int, string> _captions = new();

        // Optional: loading state per image
        public readonly HashSet<int> _captionLoading = new();

        // --- PNG generation-info (A1111 "parameters" chunk) ---
        private readonly Dictionary<int, (string Pos, string Neg)> _pngPrompts = new();

        private async Task ReadPngInfo(int index)
        {
            if (index < 0 || index >= _finalImages.Count) return;
            var bytes = await GalleryServer.GetResultBytesAsync(_finalImages[index]);
            if (bytes is null) return;
            var p = PngMetadata.ExtractParameters(bytes);
            if (string.IsNullOrWhiteSpace(p)) { _captions[index] = "No generation info found in this image."; _pngPrompts.Remove(index); return; }
            var (pos, neg) = PngMetadata.ParsePrompts(p);
            var display = PngMetadata.FormatPrompts(pos, neg);
            if (display == null) { _captions[index] = "No prompt found in this image."; _pngPrompts.Remove(index); return; }
            _captions[index] = display;
            _pngPrompts[index] = (pos, neg);
        }

        private async Task AddPngToPrompt(int index)
        {
            if (!_pngPrompts.TryGetValue(index, out var pp)) return;
            if (pp.Pos.Length > 0) _request.prompt = AppendToPrompt(_request.prompt, pp.Pos);
            if (pp.Neg.Length > 0) _request.negative_prompt = AppendToPrompt(_request.negative_prompt, pp.Neg);
            await OnPromptChanged.InvokeAsync(); // let the page refresh its bound prompt fields
            StateHasChanged();
        }

        private static string AppendToPrompt(string? current, string add)
        {
            current = (current ?? "").TrimEnd();
            if (current.Length == 0) return add;
            return current.EndsWith(",") ? current + " " + add : current + ", " + add;
        }

        private async Task InterrogateImage(int index, string model = "clip")
        {
            if (index < 0 || index >= _finalImages.Count) return;

            try
            {
                _captionLoading.Add(index);
                _captions.Clear();
                await InvokeAsync(StateHasChanged);

                // _finalImages now stores result-store ids; fetch the raw base64 (what A1111 expects).
                var b64 = await GalleryServer.GetResultBase64Async(_finalImages[index]);
                if (b64 is null) { _captions[index] = "(image no longer available)"; return; }
                var req = new InterrogateRequest
                {
                    image = b64,
                    model = model
                };

                var resp = await _httpClient.PostAsJsonAsync("sdapi/v1/interrogate", req);
                resp.EnsureSuccessStatusCode();

                var result = await resp.Content.ReadFromJsonAsync<InterrogateResponse>();
                _captions[index] = result?.caption ?? "(no caption returned)";
            }
            catch (Exception ex)
            {
                _captions[index] = $"(interrogate failed: {ex.Message})";
            }
            finally
            {
                _captionLoading.Remove(index);
                await InvokeAsync(StateHasChanged);
            }
        }

    }
}
