using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using MudBlazor;
using SimpleDiffusion.Infrastructure;

namespace SimpleDiffusion.Components
{
    public partial class Img2ImgPanel : IDisposable
    {
        // Long-edge size for the result-grid thumbnails (covers retina at the tile size).
        private const int GridThumbWidth = 768;
        [Inject] private IJSRuntime JS { get; set; } = default!;
        [Inject] private IDialogService DialogService { get; set; } = default!;
        [Inject] private IConfiguration Configuration { get; set; } = default!;
        [Inject] private ISnackbar Snackbar { get; set; } = default!;
        [Inject] private HttpClient Http { get; set; } = default!;   // app's own client (for /gallery/*)
        [Inject] private WildcardService Wildcards { get; set; } = default!;
        [Inject] private UiPreferences Prefs { get; set; } = default!;   // "Gallery image optimization" toggle
        [Inject] private HistoryService History { get; set; } = default!;
        private readonly Random _rng = new();
        [Parameter] public HttpClient _httpClient { get; set; } = default!;

        // Roll a concrete random seed (0..SeedMax) instead of -1 so the exact value is reproducible.
        private async Task RandomizeSeed()
        {
            _request.seed = _rng.NextInt64(0, Txt2ImgRequest.SeedMax + 1);
            try { await JS.InvokeVoidAsync("sdHaptic.buzz", 15); } catch { }
        }

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
        [Parameter] public TagService _tagService { get; set; } = default!;
        [Parameter] public List<SdSampler> Samplers { get; set; } = new();
        [Parameter] public List<SchedulerInfo> Schedulers { get; set; } = new();
        /// <summary>True when the SD server is unreachable — disables Generate (like txt2img/upscale).</summary>
        [Parameter] public bool ServerOffline { get; set; }

        private bool _enableQuickTags = true;
        private bool _enableFavorites = true;
        private bool _enableFrequency = true;
        private bool _enableSuggestionRibbon = true;
        private bool _isMobile = false;

        protected override void OnParametersSet()
        {
            _enableFrequency = Configuration.Flag("EnableFrequencyTracking");
            _enableFavorites = Configuration.Flag("EnableFavorites");
            _enableQuickTags = Configuration.Flag("EnableQuickTags");
            _enableSuggestionRibbon = Configuration.Flag("EnableSuggestionRibbon");
        }

        private Img2ImgRequest _request = new();
        private InitImage? _initImage;
        private List<string> _finalImages = new();
        private string _currentPreview = "";
        private int _progress = 0;
        private int _expectedTotal = 0; // images this run will produce — drives the skeleton placeholders
        private double _eta = 0; // seconds remaining (from the SD API)

        private bool _isCancelling = false;
        private CancellationTokenSource? _progressCts; // the active progress poll, so Cancel/Skip can stop it now
        private bool _isGenerating = false;
        private bool _batchActive = false; // true while a batch is running and hasn't produced its result yet

        // Captions per image (index -> caption)
        private readonly Dictionary<int, string> _captions = new();
        // Loading state per image
        private readonly HashSet<int> _captionLoading = new();

        private ResizeChoice _resizeChoice = ResizeChoice.ResizeTo;
        private double _resizeByScale = 1.0;

        private ElementReference _dropZone;
        private bool _hoverAttached;

        // ---- Inpainting ----
        private bool _inpaintMode;
        private int _brushSize = 40;
        private int _maskBlur = 4;
        private int _inpaintFill = 1;   // 1 = original
        private bool _onlyMasked;
        private bool _invertMask;       // flip white<->black if the backend masks the opposite area
        private string _maskTool = "brush";   // "brush" | "eraser" | "cursor" (cursor = don't edit the mask)
        private const int MaskMaxEdge = 1024;  // cap the exported mask so toDataURL is fast on big images

        // Recommended "Masked content" fill for the current inpaint mode: sketch seeds from the painted
        // colors (original); a plain inpaint gets the cleanest slate from a surroundings fill.
        private int InpaintRecommendedFill => _sketchMode ? 1 : 0;
        private string FillRec(int value) => value == InpaintRecommendedFill ? "  ✓ recommended" : "";

        private bool _sketchMode;                       // paint colors that steer the result (vs a plain mask)
        private string _brushColorHex = "#ff3b30";      // display color of the brush (mask exports white regardless)
        private double _maskOpacity = 0.5;              // overlay opacity for the mask painter (sketch always 1.0)
        private int _inpaintPadding = 32;               // "Only masked" crop padding, in image pixels
        private bool _showPadding;                      // outline the "Only masked" crop region on the image

        // ---- Outpainting (extend the canvas outward; mutually exclusive with inpaint) ----
        private bool _outpaintMode;
        private int _outpaintPixels = 128;
        private int _outpaintFill = 2;   // 0 fill, 1 original, 2 latent noise, 3 latent nothing
        private bool _outLeft = true, _outUp = true, _outRight = true, _outDown = true;

        // ---- ControlNet units (independent of inpaint/outpaint; can be combined with them) ----
        private List<ControlNetUnit> _cnUnits = new();

        // Live summary of what an outpaint run will produce, so the direction/size controls show effect.
        private string OutpaintSummary
        {
            get
            {
                if (_initImage == null) return "Load an image to outpaint.";
                if (_outpaintPixels <= 0 || !(_outLeft || _outUp || _outRight || _outDown))
                    return "Pick at least one direction (and an expand amount) to extend.";
                static int R8(int v) => Math.Max(8, (v + 4) / 8 * 8);
                int w = R8(_initImage.Width + (_outLeft ? _outpaintPixels : 0) + (_outRight ? _outpaintPixels : 0));
                int h = R8(_initImage.Height + (_outUp ? _outpaintPixels : 0) + (_outDown ? _outpaintPixels : 0));
                var sides = new List<string>();
                if (_outLeft) sides.Add("left");
                if (_outUp) sides.Add("up");
                if (_outDown) sides.Add("down");
                if (_outRight) sides.Add("right");
                return $"New size: {w} × {h} (from {_initImage.Width} × {_initImage.Height}) — extending {string.Join(", ", sides)}.";
            }
        }

        private DotNetObjectReference<Img2ImgPanel>? _selfRef;

        // Inline opacity for the mask canvas (sketch shows true colors, so always fully opaque there).
        private string MaskOpacityCss =>
            (_sketchMode ? 1.0 : _maskOpacity).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        [JSInvokable]
        public Task OnBrushSizeChangedJs(int size)
        {
            _brushSize = Math.Clamp(size, 5, 200);
            return InvokeAsync(StateHasChanged);
        }

        public void Dispose()
        {
            _selfRef?.Dispose();
        }

        private async Task SetMaskTool(string tool)
        {
            _maskTool = tool;
            try { await JS.InvokeVoidAsync("sdMask.setMode", _maskCanvas, tool); } catch { }
        }
        private ElementReference _initImgEl;   // the plain <img> the mask canvas overlays
        private ElementReference _maskCanvas;
        private bool _maskBound;

        private bool _initCaptionLoading = false;
        private string _initCaption = "";

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                try
                {
                    bool mobile = await JS.InvokeAsync<bool>("sdMobile.isMobile");
                    if (mobile != _isMobile)
                    {
                        _isMobile = mobile;
                        StateHasChanged();
                    }
                }
                catch { }
            }

            // Bind (or release) the inpaint mask painter to track the current init image.
            if (_inpaintMode && _initImage != null && !_maskBound)
            {
                try
                {
                    _selfRef ??= DotNetObjectReference.Create(this);
                    await JS.InvokeVoidAsync("sdMask.init", _maskCanvas, _initImgEl, _brushSize,
                        _brushColorHex, _selfRef);
                    await RefreshPadPreview();
                    _maskBound = true;
                }
                catch { }
            }
            else if ((!_inpaintMode || _initImage == null) && _maskBound)
            {
                try { await JS.InvokeVoidAsync("sdMask.destroy", _maskCanvas); } catch { }
                _maskBound = false;
            }

            if (_hoverAttached) return;

            try
            {
                await JS.InvokeVoidAsync("sdDropHover.attach", _dropZone);
                _hoverAttached = true;
            }
            catch { }
        }

        private void OnInpaintToggled(bool on)
        {
            _inpaintMode = on;
            if (on) _outpaintMode = false;   // the two masking workflows are mutually exclusive
            _maskBound = false; _maskTool = "brush";
            StateHasChanged();
        }

        private void OnSketchToggled(bool on) { _sketchMode = on; StateHasChanged(); }

        private void OnOutpaintToggled(bool on)
        {
            _outpaintMode = on;
            if (on) { _inpaintMode = false; _maskBound = false; }
            StateHasChanged();
        }

        private async Task OnBrushChanged(int v)
        {
            _brushSize = v;
            try { await JS.InvokeVoidAsync("sdMask.setBrush", _maskCanvas, v); } catch { }
        }

        private async Task OnBrushColorInput(ChangeEventArgs e)
        {
            _brushColorHex = e.Value?.ToString() ?? _brushColorHex;
            try { await JS.InvokeVoidAsync("sdMask.setColor", _maskCanvas, _brushColorHex); } catch { }
        }

        private void OnMaskOpacityChanged(double v) { _maskOpacity = v; StateHasChanged(); }

        private async Task OnOnlyMaskedChanged(bool on)
        {
            _onlyMasked = on;
            if (!on) _showPadding = false;
            await RefreshPadPreview();
        }

        private async Task OnPaddingChanged(int v) { _inpaintPadding = v; await RefreshPadPreview(); }

        private async Task OnShowPaddingChanged(bool on) { _showPadding = on; await RefreshPadPreview(); }

        private async Task RefreshPadPreview()
        {
            if (_initImage == null) return;
            try
            {
                await JS.InvokeVoidAsync("sdMask.setPaddingPreview", _maskCanvas,
                    _showPadding && _onlyMasked, _inpaintPadding, _initImage.Width, _initImage.Height);
            }
            catch { }
        }

        private async Task MaskUndo() { try { await JS.InvokeVoidAsync("sdMask.undo", _maskCanvas); } catch { } }
        private async Task MaskRedo() { try { await JS.InvokeVoidAsync("sdMask.redo", _maskCanvas); } catch { } }

        private async Task ClearMask()
        {
            try { await JS.InvokeVoidAsync("sdMask.clear", _maskCanvas); } catch { }
        }

        // ---- Mask layers (the painter is multi-layer; this mirrors the JS layer list) ----
        public class MaskLayerInfo
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            public string Color { get; set; } = "#ffffff";
            public bool Visible { get; set; } = true;
        }

        private List<MaskLayerInfo> _maskLayers = new();
        private int _activeLayerId;

        /// <summary>Pushed from JS whenever the layer list/order/metadata changes (incl. undo/redo).</summary>
        [JSInvokable]
        public Task OnLayersChanged(MaskLayerInfo[] layers, int activeId)
        {
            _maskLayers = layers?.ToList() ?? new();
            _activeLayerId = activeId;
            var active = _maskLayers.FirstOrDefault(l => l.Id == activeId);
            if (active != null) _brushColorHex = active.Color; // keep the colour picker on the active layer
            return InvokeAsync(StateHasChanged);
        }

        private async Task AddLayer() { try { await JS.InvokeVoidAsync("sdMask.addLayer", _maskCanvas, "#3b82f6", null); } catch { } }
        private async Task DeleteLayer(int id) { try { await JS.InvokeVoidAsync("sdMask.deleteLayer", _maskCanvas, id); } catch { } }
        private async Task SetActiveLayer(int id) { try { await JS.InvokeVoidAsync("sdMask.setActive", _maskCanvas, id); } catch { } }
        private async Task RenameLayer(int id, string name) { try { await JS.InvokeVoidAsync("sdMask.renameLayer", _maskCanvas, id, name); } catch { } }
        private async Task SetLayerColor(int id, string color) { try { await JS.InvokeVoidAsync("sdMask.setLayerColor", _maskCanvas, id, color); } catch { } }
        private async Task ToggleLayerVisible(int id, bool vis) { try { await JS.InvokeVoidAsync("sdMask.setLayerVisible", _maskCanvas, id, vis); } catch { } }
        private async Task MoveLayer(int id, int dir) { try { await JS.InvokeVoidAsync("sdMask.moveLayer", _maskCanvas, id, dir); } catch { } }

        // Upload an image as a new mask layer (luminance → masked; white = mask). Use "Invert layer"
        // afterwards for black-on-white masks.
        private async Task OnMaskUpload(InputFileChangeEventArgs e)
        {
            // Each selected file becomes its own mask layer (sketch mode keeps the file's own colours;
            // plain mask mode derives a single-colour mask from luminance).
            int loaded = 0, failed = 0;
            foreach (var file in e.GetMultipleFiles(20))
            {
                try
                {
                    using var ms = new MemoryStream();
                    await file.OpenReadStream(50 * 1024 * 1024).CopyToAsync(ms);
                    var dataUrl = $"data:{file.ContentType};base64,{Convert.ToBase64String(ms.ToArray())}";
                    var ok = _sketchMode
                        ? await JS.InvokeAsync<bool>("sdMask.importSketch", _maskCanvas, dataUrl, file.Name)
                        : await JS.InvokeAsync<bool>("sdMask.importLayer", _maskCanvas, dataUrl, file.Name, false);
                    if (ok) loaded++; else failed++;
                }
                catch { failed++; }
            }
            if (failed > 0)
                Snackbar.Add($"Couldn't import {failed} of {loaded + failed} image{(loaded + failed == 1 ? "" : "s")} as a mask.", Severity.Warning);
        }

        // Build a mask layer from the input image's own transparency (transparent → masked).
        private async Task MaskFromAlpha()
        {
            if (_initImage == null) return;
            try
            {
                var ok = await JS.InvokeAsync<bool>("sdMask.maskFromAlpha", _maskCanvas, InitDataUrl, "From alpha", false);
                if (!ok) Snackbar.Add("This image has no transparency to make a mask from.", Severity.Info);
            }
            catch { }
        }

        // Invert the active layer's mask (painted ↔ unpainted).
        private async Task InvertActiveLayer() { try { await JS.InvokeVoidAsync("sdMask.invertLayer", _maskCanvas, _activeLayerId); } catch { } }

        // Download the finished merged mask as a PNG (full resolution, invert applied).
        private async Task DownloadMask()
        {
            if (_initImage == null) return;
            string? mask = null;
            try { mask = await JS.InvokeAsync<string?>("sdMask.getMask", _maskCanvas, _initImage.Width, _initImage.Height, _invertMask, 0); }
            catch { }
            if (string.IsNullOrEmpty(mask)) { Snackbar.Add("Nothing painted yet — paint a mask first.", Severity.Info); return; }
            var fileName = $"mask_{Stamp.File()}.png";
            try { await JS.InvokeVoidAsync("sdDownloadBase64", mask, "image/png", fileName); } catch { }
        }

        /// <summary>Preview exactly what generation would send, in the fullscreen viewer: in Sketch mode
        /// the colour composite (init image + your colour layers); otherwise the merged white-on-black
        /// mask (invert included). Parked in the result store so it displays reliably, like the CN preview.</summary>
        private async Task PreviewMask()
        {
            if (_initImage == null) return;
            string? img = null;
            try
            {
                img = _sketchMode
                    ? await JS.InvokeAsync<string?>("sdMask.getComposite", _maskCanvas, _initImgEl, _initImage.Width, _initImage.Height, MaskMaxEdge)
                    : await JS.InvokeAsync<string?>("sdMask.getMask", _maskCanvas, _initImage.Width, _initImage.Height, _invertMask, MaskMaxEdge);
            }
            catch { }
            if (string.IsNullOrEmpty(img)) { Snackbar.Add("Nothing painted yet — paint a mask first.", Severity.Info); return; }

            string? id = null;
            try { id = await Task.Run(() => GalleryServer.SaveResult(img)); } catch { }
            var src = id != null ? $"/results/file?id={id}" : $"data:image/png;base64,{img}";

            await ImageLightbox.ShowAsync(DialogService, new List<string> { src });
        }

        private async Task SetInitImage(string base64, string mimeType)
        {
            var dims = await JS.InvokeAsync<ImageDims>(
                "sdHelpers.getImageDimensionsFromBase64",
                base64,
                mimeType);

            _initImage = new InitImage
            {
                Base64 = base64,
                MimeType = mimeType,
                Width = dims.width,
                Height = dims.height
            };

            _initCaption = "";
            _initCaptionLoading = false;
            _initPng = null; // fresh image: drop the previous PNG-info "add prompt" state
            ResetMaskModes(); // a new image invalidates the old mask/sketch/outpaint setup

            await InvokeAsync(StateHasChanged);
        }

        // A new (or removed) input image makes the previous inpaint/sketch/outpaint state meaningless —
        // turn the modes off. We deliberately leave _maskBound TRUE so OnAfterRenderAsync's destroy branch
        // (inpaint now off + bound) runs sdMask.destroy, which drops the painter, its layers AND its undo
        // history. Re-enabling inpaint then builds a fresh painter with an empty history.
        private void ResetMaskModes()
        {
            _inpaintMode = false;
            _sketchMode = false;
            _outpaintMode = false;
            _showPadding = false;
        }

        private string InitDataUrl => _initImage == null
            ? ""
            : $"data:{_initImage.MimeType};base64,{_initImage.Base64}";

        private void ClearInitImage()
        {
            _initImage = null;
            _initCaption = "";
            _initCaptionLoading = false;
            ResetMaskModes();
            StateHasChanged();
        }

        private void SwapWH()
        {
            var t = _request.width;
            _request.width = _request.height;
            _request.height = t;
        }

        private async Task OnFileSelected(InputFileChangeEventArgs e)
        {
            var file = e.File;

            using var ms = new MemoryStream();
            await file.OpenReadStream(maxAllowedSize: 50 * 1024 * 1024).CopyToAsync(ms);

            var base64 = Convert.ToBase64String(ms.ToArray());
            await SetInitImage(base64, file.ContentType);
        }

        public bool IsGenerating => _isGenerating;

        // --- PNG generation-info (A1111 "parameters" chunk) ---
        private (string Pos, string Neg)? _initPng;
        private readonly Dictionary<int, (string Pos, string Neg)> _pngPrompts = new();

        private void ReadInitPngInfo()
        {
            if (_initImage == null) return;
            var p = PngMetadata.ExtractParameters(_initImage.Base64);
            if (string.IsNullOrWhiteSpace(p)) { _initCaption = "No generation info found in this image."; _initPng = null; return; }
            var (pos, neg) = PngMetadata.ParsePrompts(p);
            var display = PngMetadata.FormatPrompts(pos, neg);
            if (display == null) { _initCaption = "No prompt found in this image."; _initPng = null; return; }
            _initCaption = display;
            _initPng = (pos, neg);
        }

        private void AddInitPngToPrompt()
        {
            if (_initPng is not { } pp) return;
            if (pp.Pos.Length > 0) _request.prompt = AppendToPrompt(_request.prompt, pp.Pos);
            if (pp.Neg.Length > 0) _request.negative_prompt = AppendToPrompt(_request.negative_prompt, pp.Neg);
            StateHasChanged();
        }

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

        private void AddPngToPrompt(int index)
        {
            if (!_pngPrompts.TryGetValue(index, out var pp)) return;
            if (pp.Pos.Length > 0) _request.prompt = AppendToPrompt(_request.prompt, pp.Pos);
            if (pp.Neg.Length > 0) _request.negative_prompt = AppendToPrompt(_request.negative_prompt, pp.Neg);
            StateHasChanged();
        }

        private static string AppendToPrompt(string? current, string add)
        {
            current = (current ?? "").TrimEnd();
            if (current.Length == 0) return add;
            return current.EndsWith(",") ? current + " " + add : current + ", " + add;
        }
        public bool HasInitImage => _initImage != null;

        public async Task GenerateImg2Img()
        {
            if (_initImage == null || _isGenerating) return;

            _isGenerating = true;
            _batchActive = true;
            _isCancelling = false;
            // Clear the previous run's results up front (don't wait for a live preview, which the
            // server may never send) so new generations replace the gallery instead of appending.
            _finalImages.Clear();
            _currentPreview = "";
            _captionLoading.Clear();
            _captions.Clear();
            _pngPrompts.Clear(); // drop stale "add prompt" data from the previous run
            try { await JS.InvokeVoidAsync("sdHaptic.buzz", 30); } catch { }

            int totalBatches = Math.Max(1, _request.n_iter);
            _expectedTotal = totalBatches * Math.Max(1, _request.batch_size); // a skeleton tile per upcoming image

            // Paint the skeleton placeholders now, before the first (blocking) request.
            await InvokeAsync(StateHasChanged);

            // Make a "single-batch" request so we can show progress per batch consistently
            var localRequest = _request.Clone();
            localRequest.n_iter = 1;

            // The image actually sent as init_images[0]. Outpaint and inpaint-sketch swap this out for a
            // derived image (padded canvas / color-composited image); plain runs keep the original.
            // Snapshot the source size too, so the loop is immune to the init image being cleared mid-run.
            string genInit = _initImage.Base64;
            string origInit = genInit;          // the untouched source (sketch/outpaint swap genInit out)
            string? sketchColoredMask = null;   // inpaint-sketch: the combined colour overlay
            LayerImage[] sketchLayers = Array.Empty<LayerImage>(); // inpaint-sketch: each layer on its own
            int initW = _initImage.Width, initH = _initImage.Height;
            bool outpaintApplied = false;

            if (_outpaintMode && _initImage != null)
            {
                if (_outpaintPixels > 0 && (_outLeft || _outUp || _outRight || _outDown))
                {
                    // Outpainting: place the image on a larger canvas and inpaint the new border region.
                    try
                    {
                        var op = await JS.InvokeAsync<OutpaintResult?>("sdMask.buildOutpaint", InitDataUrl,
                            _outLeft ? _outpaintPixels : 0, _outUp ? _outpaintPixels : 0,
                            _outRight ? _outpaintPixels : 0, _outDown ? _outpaintPixels : 0, "#7f7f7f");
                        if (op?.image != null && op.mask != null)
                        {
                            genInit = op.image;
                            localRequest.mask = op.mask;
                            localRequest.mask_blur = _maskBlur;
                            localRequest.inpainting_fill = _outpaintFill;   // how the new border is seeded
                            localRequest.inpaint_full_res = false;          // process the whole (padded) image
                            localRequest.width = op.width;
                            localRequest.height = op.height;
                            outpaintApplied = true;
                        }
                    }
                    catch { }
                    if (!outpaintApplied)
                        Snackbar.Add("Couldn't build the outpaint canvas — running a normal img2img.", Severity.Warning);
                }
                else
                {
                    Snackbar.Add("Outpaint is on but no direction/expand amount is set — running a normal img2img.", Severity.Warning);
                }
            }
            else if (_inpaintMode && _initImage != null)
            {
                // Inpainting: attach the painted mask (white-on-black, at the init image's native size).
                // Empty mask → fall through as a normal img2img run.
                try
                {
                    // One interop call: returns null when nothing is painted (→ normal img2img).
                    var mask = await JS.InvokeAsync<string?>("sdMask.getMask", _maskCanvas, _initImage.Width, _initImage.Height, _invertMask, MaskMaxEdge);
                    if (!string.IsNullOrEmpty(mask))
                    {
                        localRequest.mask = mask;
                        localRequest.mask_blur = _maskBlur;
                        localRequest.inpainting_fill = _inpaintFill;
                        localRequest.inpaint_full_res = _onlyMasked;
                        localRequest.inpaint_full_res_padding = _inpaintPadding;
                        // (invert is applied to the mask pixels client-side, so leave inpainting_mask_invert at 0)

                        // Sketch mode: send the original image with the painted colors composited on top,
                        // so the masked region is steered toward what was painted.
                        if (_sketchMode)
                        {
                            var comp = await JS.InvokeAsync<string?>("sdMask.getComposite", _maskCanvas, _initImgEl, _initImage.Width, _initImage.Height, MaskMaxEdge);
                            if (!string.IsNullOrEmpty(comp)) genInit = comp;
                            // For the History tab: the combined colour overlay + each painted layer on its own.
                            sketchColoredMask = await JS.InvokeAsync<string?>("sdMask.getColoredMask", _maskCanvas, _initImage.Width, _initImage.Height, MaskMaxEdge);
                            sketchLayers = await JS.InvokeAsync<LayerImage[]>("sdMask.getLayerImages", _maskCanvas, _initImage.Width, _initImage.Height, MaskMaxEdge) ?? Array.Empty<LayerImage>();
                        }
                    }
                    else { Snackbar.Add("Inpaint is on but no mask was painted — running a normal img2img.", Severity.Warning); }
                }
                catch { }
            }

            if (!localRequest.enable_refiner)
            {
                localRequest.refiner_checkpoint = null;
            }

            // ControlNet: attach any active units (independent of the inpaint/outpaint above — they can
            // be used together or on their own). Units that opted into "use painted inpaint mask" get the
            // current mask injected here (fetched once); the rest send no mask. Null alwayson when nothing
            // is active, so plain runs are unchanged.
            string? cnMask = null;
            if (_initImage != null && _inpaintMode && _cnUnits.Any(u => u.IsActive && u.UsePaintedMask))
            {
                try { cnMask = await JS.InvokeAsync<string?>("sdMask.getMask", _maskCanvas, _initImage.Width, _initImage.Height, _invertMask, MaskMaxEdge); } catch { }
            }
            foreach (var u in _cnUnits) u.MaskB64 = u.UsePaintedMask ? cnMask : null;
            // ControlNet units + NeverOOM (reForge memory-safety), merged into one alwayson_scripts.
            localRequest.alwayson_scripts = NeverOom.Merge(
                ControlNetUnit.BuildAlwaysOn(_cnUnits.ToArray()),
                Configuration.Flag("NeverOomUnet", false), Configuration.Flag("NeverOomVae", false),
                NeverOom.EncoderTile(Configuration), NeverOom.DecoderTile(Configuration));

            var runIds = new List<string>(); // this run's result ids, recorded to history when it finishes

            for (int i = 0; i < totalBatches; i++)
            {
                if (_isCancelling) break;

                _batchActive = true;
                _progress = 0;
                _currentPreview = "";
                using var cts = new CancellationTokenSource();
                _progressCts = cts; // so Skip/Cancel can stop the poll the instant they're pressed

                var progressTask = Task.Run(async () =>
                {
                    try
                    {
                        while (!cts.Token.IsCancellationRequested)
                        {
                            try
                            {
                                var prog = await _httpClient.GetFromJsonAsync<SdProgressResponse>(
                                    "sdapi/v1/progress",
                                    cts.Token);

                                if (prog != null)
                                {
                                    _progress = (int)(prog.progress * 100);
                                    _eta = prog.eta_relative;
                                    var candidate = prog.current_image;

                                    // Downscale + blank-check the preview on this background thread so the
                                    // client gets a small JPEG each tick, not the full-res base64.
                                    string? small = null; bool blank = false;
                                    if (!string.IsNullOrEmpty(candidate))
                                        (blank, small) = GalleryServer.PreparePreview(candidate, GridThumbWidth, Prefs.OptimizeLargeImages);

                                    await InvokeAsync(() =>
                                    {
                                        if (!blank && small != null && !cts.Token.IsCancellationRequested)
                                            _currentPreview = small;
                                        StateHasChanged();
                                    });
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                break; // cancellation is expected
                            }
                            catch
                            {
                                // ignore transient progress polling errors
                            }

                            await Task.Delay(1000, cts.Token); // 1s cadence: half the preview allocations/traffic of 500ms
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // also expected if we cancel during the Delay
                    }
                }, cts.Token);

                try
                {
                    // Resolve dynamic-prompt syntax ({a|b}, __wildcard__) fresh per image so a batch varies.
                    localRequest.prompt = DynamicPrompts.Resolve(_request.prompt, _rng, Wildcards.Get);
                    localRequest.negative_prompt = DynamicPrompts.Resolve(_request.negative_prompt, _rng, Wildcards.Get);

                    // A1111 expects list even for one init image
                    localRequest.init_images = new List<string> { genInit };

                    if (outpaintApplied)
                    {
                        // Outpaint already set the (padded) output size; don't override it.
                    }
                    else if (_resizeChoice == ResizeChoice.ResizeBy)
                    {
                        localRequest.width = RoundTo64(initW * _resizeByScale);
                        localRequest.height = RoundTo64(initH * _resizeByScale);
                    }
                    else if (localRequest.mask != null)
                    {
                        // Inpaint/sketch: a square Resize-To (the 1024×1024 default) squishes a non-square
                        // image and misaligns the mask → garbage. Match the image's aspect ratio while
                        // keeping the user's pixel budget (width×height) so the resolution stays sane.
                        double budget = (double)_request.width * _request.height;
                        double ar = (double)initW / Math.Max(1, initH);
                        localRequest.width = Math.Max(64, RoundTo8((int)Math.Round(Math.Sqrt(budget * ar))));
                        localRequest.height = Math.Max(64, RoundTo8((int)Math.Round(Math.Sqrt(budget / ar))));
                    }

                    var resp = await _httpClient.PostAsJsonAsync("sdapi/v1/img2img", localRequest);
                    resp.EnsureSuccessStatusCode();

                    var result = await resp.Content.ReadFromJsonAsync<Txt2ImgResponse>();
                    if (result?.images != null)
                    {
                        // Persist outputs to the result store off-thread; keep only ids (#5).
                        var ids = await Task.Run(() => GalleryServer.SaveResults(result.images));
                        _finalImages.AddRange(ids);
                        runIds.AddRange(ids);
                        foreach (var id in ids) _ = GalleryServer.WarmResultTierAsync(id, GridThumbWidth);
                    }

                    // The finished image is now in the gallery — drop the in-progress preview
                    // and mark the batch done so neither the preview card nor the "starting"
                    // progress bar flashes while the loop finishes up.
                    _currentPreview = "";
                    _batchActive = false;

                    await InvokeAsync(StateHasChanged);
                }
                catch (Exception ex)
                {
                    // No catch here previously meant a server/network failure tore down the circuit.
                    if (!_isCancelling)
                        Snackbar.Add($"Img2Img failed: {ex.Message}", Severity.Error);
                }
                finally
                {
                    cts.Cancel();
                    try { await progressTask; } catch { }
                }
            }

            if (_finalImages.Any() && _tagService != null && _request != null && _enableFrequency)
            {
                _tagService.IncrementPromptTags(_request.prompt);
            }

            // Record this img2img run to the per-device history so a refresh/crash can recover it.
            if (runIds.Count > 0 && _request != null)
            {
                // For a masked run (inpaint/outpaint), also stash the source image + mask(s) used so they
                // show in the History tab and the user can download them to reuse. Inpaint-sketch saves the
                // untouched source + each painted layer (and a combined overlay); the rest save the init
                // sent + the single B&W mask.
                string? sourceId = null;
                var masks = new List<MaskRef>();
                if (!string.IsNullOrEmpty(localRequest.mask))
                {
                    async Task<string?> Save(string? b64)
                    {
                        if (string.IsNullOrEmpty(b64)) return null;
                        try { return await Task.Run(() => GalleryServer.SaveResult(b64)); } catch { return null; }
                    }

                    bool sketch = _inpaintMode && _sketchMode && sketchLayers.Length > 0;
                    if (sketch)
                    {
                        sourceId = await Save(origInit);   // the untouched source, without the sketch
                        for (int li = 0; li < sketchLayers.Length; li++)
                        {
                            var id = await Save(sketchLayers[li].image);
                            if (id != null) masks.Add(new MaskRef { Id = id, Label = string.IsNullOrWhiteSpace(sketchLayers[li].name) ? $"Mask {li + 1}" : sketchLayers[li].name! });
                        }
                        // A combined overlay too, when there's more than one layer (a single layer already is it).
                        if (sketchLayers.Length > 1 && !string.IsNullOrEmpty(sketchColoredMask))
                        {
                            var id = await Save(sketchColoredMask);
                            if (id != null) masks.Add(new MaskRef { Id = id, Label = "Combined" });
                        }
                    }
                    else
                    {
                        sourceId = await Save(genInit);
                        var id = await Save(localRequest.mask);
                        if (id != null) masks.Add(new MaskRef { Id = id, Label = "Mask" });
                    }
                }

                await History.AddAsync(new HistoryEntry
                {
                    Source = "Img2Img",
                    Prompt = _request.prompt,
                    Negative = _request.negative_prompt,
                    Seed = _request.seed,
                    Steps = _request.steps,
                    Cfg = _request.cfg_scale,
                    Sampler = _request.sampler_name,
                    Scheduler = _request.scheduler,
                    Width = _request.width,
                    Height = _request.height,
                    Denoise = _request.denoising_strength,
                    ResultIds = runIds,
                    SourceId = sourceId,
                    Masks = masks,
                });
            }

            _isGenerating = false;
            _batchActive = false;
            _isCancelling = false;
            _currentPreview = "";
            _expectedTotal = 0;
            await InvokeAsync(StateHasChanged);
        }

        private static int RoundTo64(double v) => Math.Max(64, (int)(v / 64) * 64);
        private static int RoundTo8(int v) => Math.Max(8, (int)Math.Round(v / 8.0) * 8);

        // ---- Resolution: aspect-ratio lock + "match image" ----
        private bool _lockAspect;
        private double _lockedAspect = 1.0;

        private void OnLockAspectChanged(bool on)
        {
            _lockAspect = on;
            if (on && _request.height > 0) _lockedAspect = (double)_request.width / _request.height;
        }

        private void OnWidthChanged(int w)
        {
            _request.width = w;
            if (_lockAspect && _lockedAspect > 0) _request.height = RoundTo64(w / _lockedAspect);
        }

        private void OnHeightChanged(int h)
        {
            _request.height = h;
            if (_lockAspect && _lockedAspect > 0) _request.width = RoundTo64(h * _lockedAspect);
        }

        // Set the output dimensions to the loaded image's size (rounded to a multiple of 64).
        private void MatchImageResolution()
        {
            if (_initImage == null) return;
            _request.width = RoundTo64(_initImage.Width);
            _request.height = RoundTo64(_initImage.Height);
            if (_request.height > 0) _lockedAspect = (double)_request.width / _request.height;
        }

        private enum ResizeChoice { ResizeTo, ResizeBy }

        // One sketch layer returned by sdMask.getLayerImages (name + base64 PNG).
        private sealed class LayerImage
        {
            public string? name { get; set; }
            public string image { get; set; } = "";
        }

        private class Img2ImgRequest
        {
            public List<string> init_images { get; set; } = new();

            public string prompt { get; set; } = "";
            public string negative_prompt { get; set; } = "";

            public int resize_mode { get; set; } = 0;

            public int width { get; set; } = 1024;
            public int height { get; set; } = 1024;

            public double cfg_scale { get; set; } = 7.0;
            public double cfg_rescale { get; set; } = 0.0;
            public double denoising_strength { get; set; } = 0.75;

            public int steps { get; set; } = 20;

            public string sampler_name { get; set; } = "DPM++ 2M";
            public string scheduler { get; set; } = "Automatic";
            public long seed { get; set; } = -1;
            public int n_iter { get; set; } = 1;
            public int batch_size { get; set; } = 1;

            // Inpainting (null mask = ordinary img2img). The mask is white-on-black at the init size.
            // Omitted from the JSON when null so a normal img2img request is unchanged.
            [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
            public string? mask { get; set; }
            public int mask_blur { get; set; } = 4;
            public int inpainting_fill { get; set; } = 1;       // 0 fill, 1 original, 2 latent noise, 3 latent nothing
            public bool inpaint_full_res { get; set; }           // "Only masked" — process just the masked region
            public int inpaint_full_res_padding { get; set; } = 32;
            public int inpainting_mask_invert { get; set; }      // 0 inpaint masked, 1 inpaint everything else

            // Refiner parameters
            public bool enable_refiner { get; set; } = false;
            public string? refiner_checkpoint { get; set; } = null;
            public double refiner_switch_at { get; set; } = 0.8;

            // ControlNet (alwayson scripts). Null = omitted, so non-ControlNet runs are unchanged.
            [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
            public object? alwayson_scripts { get; set; }

            public Img2ImgRequest Clone() => new Img2ImgRequest
            {
                init_images = new List<string>(init_images),
                prompt = prompt,
                negative_prompt = negative_prompt,
                resize_mode = resize_mode,
                width = width,
                height = height,
                cfg_scale = cfg_scale,
                cfg_rescale = cfg_rescale,
                denoising_strength = denoising_strength,
                steps = steps,
                sampler_name = sampler_name,
                scheduler = scheduler,
                seed = seed,
                n_iter = n_iter,
                batch_size = batch_size,
                mask = mask,
                mask_blur = mask_blur,
                inpainting_fill = inpainting_fill,
                inpaint_full_res = inpaint_full_res,
                inpaint_full_res_padding = inpaint_full_res_padding,
                inpainting_mask_invert = inpainting_mask_invert,
                enable_refiner = enable_refiner,
                refiner_checkpoint = refiner_checkpoint,
                refiner_switch_at = refiner_switch_at,
                alwayson_scripts = alwayson_scripts
            };
        }

        private class ImageDims
        {
            public int width { get; set; }
            public int height { get; set; }
        }

        // Result of sdMask.buildOutpaint — the padded init image, its mask, and the new output size.
        private class OutpaintResult
        {
            public string image { get; set; } = "";
            public string mask { get; set; } = "";
            public int width { get; set; }
            public int height { get; set; }
        }

        private class InitImage
        {
            public string Base64 { get; set; } = "";
            public string MimeType { get; set; } = "image/png";
            public int Width { get; set; }
            public int Height { get; set; }
        }

        private static string ToPngDataUrl(string b64) => $"data:image/png;base64,{b64}";

        public async Task UseAsInitImageFromTxt2Img(string base64Png)
        {
            await SetInitImage(base64Png, "image/png");
        }

        /// <summary>This panel's live ControlNet unit list (for the "Send → ControlNet" picker).</summary>
        public List<ControlNetUnit> ControlNetUnits => _cnUnits;

        /// <summary>Re-render after the unit list was changed externally (e.g. by the send picker).</summary>
        public void NotifyControlNetChanged() => StateHasChanged();

        private void OpenLightbox(int index)
            => ImageLightbox.ShowAsync(DialogService, _finalImages.Select(id => $"/results/file?id={id}").ToList(), index);

        private async Task DownloadAllAsZip()
        {
            if (!_finalImages.Any()) return;

            var stamp = Stamp.File();
            var files = new List<(string, string)>();
            for (int i = 0; i < _finalImages.Count; i++)
            {
                var b64 = await GalleryServer.GetResultBase64Async(_finalImages[i]);
                if (b64 is not null) files.Add(($"SD_{stamp}_{i + 1}.png", b64));
            }
            if (files.Count == 0) return;
            var zipName = $"SD_Img2Img_{stamp}.zip";
            await JS.InvokeVoidAsync("downloadUrl", GalleryServer.BuildZipDownload(files), zipName);
        }

        public class InterrogateRequest
        {
            public string image { get; set; } = "";
            public string model { get; set; } = "clip";
        }
        public class InterrogateResponse
        {
            public string caption { get; set; } = "";
        }

        private async Task InterrogateImage(int index, string model = "clip")
        {
            if (index < 0 || index >= _finalImages.Count) return;

            _captionLoading.Clear();
            _captions.Clear();

            try
            {
                _captionLoading.Add(index);
                await InvokeAsync(StateHasChanged);

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

        private async Task InterrogateInitImage(string model)
        {
            if (_initImage == null) return;

            try
            {
                _initCaptionLoading = true;
                _initCaption = "";
                await InvokeAsync(StateHasChanged);

                var req = new InterrogateRequest
                {
                    image = _initImage.Base64,
                    model = model
                };

                var resp = await _httpClient.PostAsJsonAsync("sdapi/v1/interrogate", req);
                resp.EnsureSuccessStatusCode();

                var result = await resp.Content.ReadFromJsonAsync<InterrogateResponse>();
                _initCaption = result?.caption ?? "(no caption returned)";
            }
            catch (Exception ex)
            {
                _initCaption = $"(interrogate failed: {ex.Message})";
            }
            finally
            {
                _initCaptionLoading = false;
                await InvokeAsync(StateHasChanged);
            }
        }

        private async Task Interrupt()
        {
            _isCancelling = true;
            try { _progressCts?.Cancel(); } catch { } // stop polling/preview churn now (cts may be disposed between batches)
            StateHasChanged();                         // show the "Stopping…" state immediately
            try { await _httpClient.PostAsync("sdapi/v1/interrupt", null); } catch { }
        }

        private async Task Skip()
        {
            try { await _httpClient.PostAsync("sdapi/v1/skip", null); } catch { }
        }

        public void ApplyPreset(GenerationPreset preset)
        {
            _request.prompt = preset.Prompt;
            _request.negative_prompt = preset.NegativePrompt;
            _request.steps = preset.Steps;
            _request.sampler_name = preset.SamplerName;
            _request.scheduler = preset.Scheduler;
            _request.cfg_scale = preset.CfgScale;
            _request.cfg_rescale = preset.CfgRescale;
            _request.width = preset.Width;
            _request.height = preset.Height;
            _request.denoising_strength = preset.DenoisingStrength;
            _request.enable_refiner = preset.EnableRefiner;
            _request.refiner_checkpoint = preset.RefinerCheckpoint;
            _request.refiner_switch_at = preset.RefinerSwitchAt;
            _cnUnits.Clear();
            _cnUnits.AddRange((preset.ControlNetUnits ?? new()).Select(u => u.Clone()));
            StateHasChanged();
        }

        public void SetPrompts(string prompt, string negativePrompt)
        {
            _request.prompt = prompt;
            _request.negative_prompt = negativePrompt;
            StateHasChanged();
        }

        public GenerationPreset ToPreset(string name, string checkpoint)
        {
            return new GenerationPreset
            {
                Name = name,
                Checkpoint = checkpoint,
                Prompt = _request.prompt,
                NegativePrompt = _request.negative_prompt,
                Steps = _request.steps,
                SamplerName = _request.sampler_name,
                Scheduler = _request.scheduler,
                CfgScale = _request.cfg_scale,
                CfgRescale = _request.cfg_rescale,
                Width = _request.width,
                Height = _request.height,
                DenoisingStrength = _request.denoising_strength,
                EnableRefiner = _request.enable_refiner,
                RefinerCheckpoint = _request.refiner_checkpoint,
                RefinerSwitchAt = _request.refiner_switch_at,
                ControlNetUnits = _cnUnits.Select(u => u.Clone()).ToList()
            };
        }
    }
}
