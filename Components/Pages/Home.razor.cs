using MudBlazor;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using System.Text.Json;
using System.IO;
using SimpleDiffusion.Infrastructure;

namespace SimpleDiffusion.Components.Pages
{
    public partial class Home : IDisposable
    {
        // Long timeout: SD generations/upscales/hires can easily exceed HttpClient's 100s default,
        // which would otherwise drop the request mid-render.
        static readonly TimeSpan SdHttpTimeout = TimeSpan.FromHours(1);
        HttpClient _httpClient = new HttpClient { Timeout = SdHttpTimeout };
        string SD_ServerAddress = "http://192.168.0.92:7860/";
        string LoraPath = @"C:\ai\stable-diffusion-webui-reForge\models\Lora";
        string TagPath = @"C:\Users\Tohru\Documents\Visual Studio 2022\Projects\SimpleDiffusion\wwwroot\tags";
        List<SdModel> Models = new List<SdModel>();
        List<SdSampler> Samplers = new List<SdSampler>();
        List<UpscalerInfo> Upscalers = new List<UpscalerInfo>();
        List<SchedulerInfo> Schedulers = new List<SchedulerInfo>();

        [Inject] private IDialogService DialogService { get; set; } = default!;
        [Inject] private IConfiguration Configuration { get; set; } = default!;
        [Inject] private IJSRuntime JS { get; set; } = default!;

        [Inject] private WorkspaceState Workspace { get; set; } = default!;
        [Inject] private CrossTab CrossTab { get; set; } = default!;
        [Inject] private UiPreferences UiPrefs { get; set; } = default!;
        [Inject] private ISnackbar Snackbar { get; set; } = default!;

        // Top-level tab indices: 0=Txt2Img, 1=Img2Img, 2=Upscale, 3=LoRA, 4=Gallery, 5=History,
        // 6=Civitai, 7=Server.
        private const int TabTxt2Img = 0;
        private const int TabImg2Img = 1;
        private const int TabUpscale = 2;
        private const int TabLora = 3;
        private const int TabGallery = 4;
        private const int TabHistory = 5;
        private const int TabServer = 7;

        // These three are backed by the scoped WorkspaceState so they persist across
        // navigation (e.g. visiting Settings) instead of resetting when Home is recreated.
        int _activeTabIndex { get => Workspace.ActiveTabIndex; set => Workspace.ActiveTabIndex = value; }
        string _selectedPresetName { get => Workspace.SelectedPresetName; set => Workspace.SelectedPresetName = value; }
        Txt2ImgRequest _request => Workspace.Request;

        // A/B comparison mode (Txt2Img). CurrentRequest is the set the controls currently edit.
        Txt2ImgRequest _requestB => Workspace.RequestB;
        bool _abEnabled { get => Workspace.AbEnabled; set => Workspace.AbEnabled = value; }
        int _abActive { get => Workspace.AbActive; set => Workspace.AbActive = value; }
        Txt2ImgRequest CurrentRequest => _abEnabled && _abActive == 1 ? _requestB : _request;

        // ControlNet units for the set currently being edited (parallel to CurrentRequest).
        List<ControlNetUnit> CurrentCnUnits => _abEnabled && _abActive == 1 ? Workspace.ControlNetUnitsB : Workspace.ControlNetUnitsA;

        // "Surprise Me" randomness (not saved with presets; reset by Default Settings).
        int _surpriseCraziness { get => Workspace.SurpriseCraziness; set => Workspace.SurpriseCraziness = value; }
        // When on, Surprise Me keeps the user's own prompts and adds the random tags on top.
        bool _surpriseUsePrompts { get => Workspace.SurpriseUsePrompts; set => Workspace.SurpriseUsePrompts = value; }

        List<GenerationPreset> Presets = new List<GenerationPreset>();
        // Shared app-wide singleton: the 221k-tag dictionary + trigram index is large and identical
        // for every client, so it's built once and reused — not rebuilt per circuit. (Was `new
        // TagService()` per connection, which cost ~150-200MB and seconds of CPU on every connect.)
        [Inject] private TagService _tagService { get; set; } = default!;

        public string _selectedModel = "";
        bool _loadingModel = false;
        ResultsGallery resultsGallery;
        AbComparePanel abPanel;
        Img2ImgPanel img2imgPanel;
        SimpleDiffusion.Components.Tabs.UpscaleTab upscaleTab;
        bool offline = false; // assume ok until a connection attempt says otherwise (avoids a banner flash on load)

        bool _enableQuickTags = true;
        bool _enableFavorites = true;
        bool _enableFrequency = true;

        // Mobile feature toggles + device detection.
        bool _enableSuggestionRibbon = true;
        bool _enableMobileNav = true;
        bool _isMobile = false;

        protected override void OnInitialized()
        {
            // Reload settings-derived state when the Settings dialog saves, without recreating
            // the page (so prompt, settings, Img2Img inputs and generated images are preserved).
            Workspace.SettingsSaved += HandleSettingsSaved;
            CrossTab.JumpToLoraRequested += HandleJumpToLora;
            CrossTab.SendToImg2ImgRequested += HandleSendToImg2ImgRequested;
            CrossTab.SendToUpscaleRequested += HandleSendToUpscaleRequested;
            CrossTab.SendToControlNetRequested += HandleSendToControlNetRequested;
            CrossTab.VariationsRequested += HandleVariationsRequested;
        }

        // From the Gallery / fullscreen viewer: load a base64 image as the Img2Img input and switch tabs.
        private async void HandleSendToImg2ImgRequested(string base64Png)
        {
            await InvokeAsync(async () =>
            {
                try { await JS.InvokeVoidAsync("sdCloseDialogs"); } catch { } // close the viewer / any open dialog
                if (img2imgPanel != null)
                {
                    await img2imgPanel.UseAsInitImageFromTxt2Img(base64Png);

                    // Carry over the prompt/negative the image was generated with (from its PNG
                    // metadata), so a txt2img → Img2Img handoff keeps the prompt. No-op for images
                    // without embedded parameters (e.g. plain uploads or upscaler output).
                    var raw = PngMetadata.ExtractParameters(base64Png);
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        var (pos, neg) = PngMetadata.ParsePrompts(raw);
                        if (!string.IsNullOrWhiteSpace(pos) || !string.IsNullOrWhiteSpace(neg))
                            img2imgPanel.SetPrompts(pos, neg);
                    }

                    _activeTabIndex = TabImg2Img;
                    StateHasChanged();
                }
            });
        }

        private VariationsRequest? _pendingVariation;

        // From the fullscreen viewer: reproduce an image (its prompt + base seed) but vary the
        // subseed so each result is a sibling of the original. Lands on the Txt2Img tab and runs.
        private async void HandleVariationsRequested(VariationsRequest req)
        {
            await InvokeAsync(async () =>
            {
                try { await JS.InvokeVoidAsync("sdCloseDialogs"); } catch { } // close the viewer

                bool wasAb = _abEnabled;
                _abEnabled = false;       // variations are a single-image flow → use the normal gallery
                _activeTabIndex = TabTxt2Img;

                // If we were in A/B mode, the normal gallery isn't mounted yet — defer the run to
                // after the next render (OnAfterRenderAsync) so its @ref is available.
                if (wasAb || resultsGallery == null)
                {
                    _pendingVariation = req;
                    StateHasChanged();
                    return;
                }

                await RunVariation(req);
            });
        }

        private async Task RunVariation(VariationsRequest req)
        {
            if (!string.IsNullOrWhiteSpace(req.Prompt)) _request.prompt = req.Prompt;
            _request.negative_prompt = req.Negative ?? "";
            _request.seed = req.Seed;            // lock the base seed
            _request.subseed = -1;               // fresh variation seed per image
            _request.subseed_strength = req.Strength;
            _request.n_iter = Math.Clamp(req.Count, 1, 8);

            // Keep the source image in the gallery, with the new variations appended after it.
            resultsGallery?.StartVariationGallery(req.OriginalBase64);
            StateHasChanged();
            if (resultsGallery != null)
                await resultsGallery.GenerateImageWithProgress(keepExisting: true);

            // Variations are a one-shot action — clear the seed/subseed lock so the next normal
            // Generate doesn't silently keep producing variations (there's no UI for it here).
            _request.seed = -1;
            _request.subseed = -1;
            _request.subseed_strength = 0;
            StateHasChanged();
        }

        // From any "Send → Upscale" action: queue the image on the Upscale tab and switch to it.
        private async void HandleSendToUpscaleRequested(string base64Png)
        {
            await InvokeAsync(async () =>
            {
                try { await JS.InvokeVoidAsync("sdCloseDialogs"); } catch { } // close the viewer / any dialog
                if (upscaleTab != null)
                {
                    await upscaleTab.AddImage(base64Png);
                    _activeTabIndex = TabUpscale;
                    StateHasChanged();
                }
            });
        }

        // From any "Send → ControlNet" action: ask which unit(s) — on either page — should receive the
        // image (or add a new one), then apply and switch to the first affected page.
        private async void HandleSendToControlNetRequested(string base64)
        {
            await InvokeAsync(async () =>
            {
                // Note: the fullscreen viewer (when the send came from there) closes itself via the Send
                // menu's OnSent — don't call sdCloseDialogs here or it would also close this picker.
                var txtUnits = CurrentCnUnits;
                var targets = new List<CnSendTarget>
                {
                    new() { Page = _abEnabled ? $"Txt2Img (Set {(_abActive == 1 ? "B" : "A")})" : "Txt2Img", Units = txtUnits },
                };
                var imgUnits = img2imgPanel?.ControlNetUnits;
                if (imgUnits != null) targets.Add(new() { Page = "Img2Img", Units = imgUnits });

                var options = new DialogOptions { CloseOnEscapeKey = true, MaxWidth = MaxWidth.ExtraSmall, FullWidth = true };
                var parameters = new DialogParameters<SimpleDiffusion.Components.SendToControlNetDialog> { { x => x.Targets, targets } };
                var dialog = await DialogService.ShowAsync<SimpleDiffusion.Components.SendToControlNetDialog>("Send to ControlNet", parameters, options);
                var result = await dialog.Result;
                if (result is null || result.Canceled || result.Data is not CnSendResult sel || !sel.Any) return;

                foreach (var u in sel.Units) u.ImageB64 = base64;
                foreach (var t in sel.AddTo) t.Units.Add(new ControlNetUnit { Enabled = true, ImageB64 = base64 });

                // Txt2Img mirrors units onto its request here; Img2Img builds them at generation time.
                bool txtAffected = sel.Units.Any(u => txtUnits.Contains(u)) || sel.AddTo.Any(t => t.Units == txtUnits);
                if (txtAffected) CurrentRequest.alwayson_scripts = ControlNetUnit.BuildAlwaysOn(txtUnits.ToArray());

                bool imgAffected = imgUnits != null && (sel.Units.Any(u => imgUnits.Contains(u)) || sel.AddTo.Any(t => t.Units == imgUnits));
                if (imgAffected) img2imgPanel?.NotifyControlNetChanged();

                if (txtAffected) _activeTabIndex = TabTxt2Img;
                else if (imgAffected) _activeTabIndex = TabImg2Img;

                Snackbar.Add($"Sent image to {sel.Units.Count + sel.AddTo.Count} ControlNet unit(s).", Severity.Success);
                StateHasChanged();
            });
        }

        private async void HandleJumpToLora(string _)
        {
            // Reveal the (now top-level) LoRA browser tab.
            await InvokeAsync(() =>
            {
                _activeTabIndex = TabLora;
                StateHasChanged();
            });
        }

        private DotNetObjectReference<Home>? _selfRef;

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                try
                {
                    _selfRef ??= DotNetObjectReference.Create(this);
                    await JS.InvokeVoidAsync("sdHotkeys.register", _selfRef); // Ctrl/Cmd+Enter → generate
                }
                catch { /* JS not ready / prerender */ }
                try
                {
                    bool mobile = await JS.InvokeAsync<bool>("sdMobile.isMobile");
                    if (mobile != _isMobile)
                    {
                        _isMobile = mobile;
                        StateHasChanged();
                    }
                }
                catch { /* JS not ready / prerender */ }
            }

            // A variation was requested while in A/B mode; the normal gallery is now mounted.
            if (_pendingVariation is { } pv && resultsGallery != null)
            {
                _pendingVariation = null;
                await RunVariation(pv);
            }
        }

        /// <summary>Ctrl/Cmd+Enter: generate on the active tab (no-op on non-generation tabs).</summary>
        [JSInvokable]
        public Task GenerateHotkey() => InvokeAsync(async () =>
        {
            if (_activeTabIndex == TabTxt2Img || _activeTabIndex == TabImg2Img)
                await TriggerGenerate();
        });

        private async void HandleSettingsSaved()
        {
            // Settings may have changed custom tags / LoRA folder content without changing the load
            // signature, so force the shared tag data to rebuild on the next load.
            _tagService.Invalidate();
            await InvokeAsync(async () =>
            {
                await LoadWorkspaceAsync();
                StateHasChanged();
            });
        }

        public void Dispose()
        {
            Workspace.SettingsSaved -= HandleSettingsSaved;
            CrossTab.JumpToLoraRequested -= HandleJumpToLora;
            CrossTab.SendToImg2ImgRequested -= HandleSendToImg2ImgRequested;
            CrossTab.SendToUpscaleRequested -= HandleSendToUpscaleRequested;
            CrossTab.SendToControlNetRequested -= HandleSendToControlNetRequested;
            CrossTab.VariationsRequested -= HandleVariationsRequested;
            try { _ = JS.InvokeVoidAsync("sdHotkeys.unregister"); } catch { }
            _selfRef?.Dispose();
        }

        protected override async Task OnParametersSetAsync()
        {
            await LoadWorkspaceAsync();
        }

        private async Task LoadWorkspaceAsync()
        {
            SD_ServerAddress = Configuration["SdServerAddress"] ?? "http://192.168.0.92:7860/";
            LoraPath = Configuration["BaseLoraPath"] ?? @"C:\ai\stable-diffusion-webui-reForge\models\Lora";
            TagPath = Configuration["TagPath"] ?? @"C:\Users\Tohru\Documents\Visual Studio 2022\Projects\SimpleDiffusion\wwwroot\tags";

            _enableFrequency = Configuration.Flag("EnableFrequencyTracking");
            _enableFavorites = Configuration.Flag("EnableFavorites");
            _enableQuickTags = Configuration.Flag("EnableQuickTags");

            _enableSuggestionRibbon = Configuration.Flag("EnableSuggestionRibbon");
            _enableMobileNav = Configuration.Flag("EnableMobileNav");

            // Recreate the client: BaseAddress can't be changed once a client has sent requests,
            // and this method now also runs on settings save (when the address may have changed).
            var oldClient = _httpClient;
            _httpClient = new HttpClient { BaseAddress = new Uri(SD_ServerAddress), Timeout = SdHttpTimeout };
            oldClient?.Dispose();

            // Server-dependent data: only available when the SD API is reachable.
            if (await TryConnect())
            {
                await ReloadServerDataAsync();
            }

            // File-based data (tags, LoRAs, presets) comes from the local filesystem — always load
            // it, even with the server offline, so the prompt helper / autocomplete still works.
            // Shared + load-once: only (re)builds the big index when these inputs actually change.
            var selDict = Configuration["SelectedDictionary"] ?? "All";
            var customFileName = Configuration["CustomTagsFileName"] ?? "custom_tags.txt";
            await _tagService.EnsureLoadedAsync(TagPath, selDict, customFileName, LoraPath);

            LoadPresets();
        }

        private async Task RetryConnect()
        {
            await LoadWorkspaceAsync();
            StateHasChanged();
        }

        // Loads the data that requires the SD API to be reachable.
        private async Task ReloadServerDataAsync()
        {
            await GetModelsAsync();
            _selectedModel = await GetCurrentModelAsync();
            await GetSamplersAsync();
            await GetUpscalersAsync();
            await GetSchedulersAsync();
        }

        public async Task<bool> TryConnect()
        {
            try
            {
                // Probe a real API endpoint and require a success status. Just hitting the socket
                // isn't enough — right after the server starts it accepts connections but the API
                // routes aren't registered yet (they return 404), which then breaks the data loads.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var res = await _httpClient.GetAsync("sdapi/v1/progress", cts.Token);
                offline = !res.IsSuccessStatusCode;
                return res.IsSuccessStatusCode;
            }
            catch (Exception)
            {
                offline = true;
                return false;
            }
        }

        public async Task GetModelsAsync()
        {
            try
            {
                var models = await _httpClient.GetFromJsonAsync<List<SdModel>>("sdapi/v1/sd-models");

                if (models == null)
                    models = new List<SdModel>();

                Models = models.ToList();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching models: {ex.Message}");
                Models = new List<SdModel>();
            }
        }

        public async Task<string> GetCurrentModelAsync()
        {
            try
            {
                // The 'options' endpoint returns the current configuration
                var options = await _httpClient.GetFromJsonAsync<Dictionary<string, object>>("sdapi/v1/options");
                if (options != null && options.TryGetValue("sd_model_checkpoint", out var model))
                {
                    return model?.ToString() ?? "";
                }
            }
            catch (Exception ex) { Console.WriteLine($"Error fetching current model: {ex.Message}"); }
            return "";
        }

        public async Task<bool> SetModelAsync(string modelTitle)
        {
            var payload = new { sd_model_checkpoint = modelTitle };
            var response = await _httpClient.PostAsJsonAsync("sdapi/v1/options", payload);
            return response.IsSuccessStatusCode;
        }

        private async Task OnModelChanged(string newModel)
        {
            if (_selectedModel == newModel) return;

            _loadingModel = true;
            _selectedModel = newModel;
            StateHasChanged();

            try
            {
                var success = await SetModelAsync(newModel);
                if (!success)
                {
                    // Handle error (perhaps show a MudSnackbar)
                }
            }
            finally
            {
                _loadingModel = false;
                StateHasChanged();
            }
        }

        public async Task GetSamplersAsync()
        {
            try
            {
                Samplers = await _httpClient.GetFromJsonAsync<List<SdSampler>>("sdapi/v1/samplers") ?? new List<SdSampler>();

                // Set a default if nothing is selected
                if (string.IsNullOrEmpty(_request.sampler_name) && Samplers.Any())
                {
                    _request.sampler_name = "DPM++ 2M";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching samplers: {ex.Message}");
                Samplers = new List<SdSampler>();
            }
        }

        // Fetch available upscalers for Hires. Fix
        public async Task GetUpscalersAsync()
        {
            try
            {
                var result = await _httpClient.GetFromJsonAsync<List<UpscalerInfo>>("sdapi/v1/upscalers");
                Upscalers = result ?? new List<UpscalerInfo>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching upscalers: {ex.Message}");
                Upscalers = new List<UpscalerInfo>();
            }
        }

        // Fetch available schedulers (Karras, Exponential, etc)
        public async Task GetSchedulersAsync()
        {
            try
            {
                var result = await _httpClient.GetFromJsonAsync<List<SchedulerInfo>>("sdapi/v1/schedulers");
                Schedulers = result ?? new List<SchedulerInfo>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching schedulers: {ex.Message}");
                Schedulers = new List<SchedulerInfo>();
            }
        }

        private void SwapDimensions()
        {
            var r = CurrentRequest;
            (r.width, r.height) = (r.height, r.width);
        }

        private void ToggleLoraInPrompt(LoraSelection selection)
        {
            var name = selection.Name;
            Snackbar.Clear(); // drop the previous add/remove toast so they don't stack up
            // Regex breakdown:
            // (?i)        : Case-insensitive
            // <lora:{name}: : The start of your tag
            // [^>]+       : Match any characters that ARE NOT a '>' (the weight)
            // >           : The closing bracket
            // \s?         : Optional trailing space to keep the prompt clean
            string pattern = $@"(?i)<lora:{Regex.Escape(name)}:[^>]+>\s?";

            if (Regex.IsMatch(_request.prompt, pattern))
            {
                // 1. REMOVE: It exists with some weight, so we strip the whole thing
                _request.prompt = Regex.Replace(_request.prompt, pattern, string.Empty).Trim();
                // The Txt2Img prompt isn't visible from the LoRA tab, so confirm the change.
                Snackbar.Add($"Removed {name} from the prompt", Severity.Info);
            }
            else
            {
                // 2. ADD: It doesn't exist, append it
                const string defaultWeight = "1.0";
                string loraTag = $" <lora:{name}:{defaultWeight}>";
                _request.prompt = (_request.prompt.Trim() + loraTag).Trim();
                Snackbar.Add($"Added {name} to the prompt", Severity.Success);

                // Also append the LoRA's trigger words (skipping any already present), if enabled.
                if (UiPrefs.AutoAddTriggerWords && !string.IsNullOrWhiteSpace(selection.Triggers))
                {
                    foreach (var word in selection.Triggers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (_request.prompt.Contains(word, StringComparison.OrdinalIgnoreCase)) continue;
                        _request.prompt = _request.prompt.TrimEnd().TrimEnd(',') + ", " + word;
                    }
                }
            }

            StateHasChanged();
        }

        private void ResetPrompts()
        {
            // Default Settings should clear the prompts back to empty (the real default), not seed
            // any specific prompt/LoRAs.
            var r = CurrentRequest;
            r.prompt = "";
            r.negative_prompt = "";
            _surpriseCraziness = WorkspaceState.SurpriseCrazinessDefault; // reset the Surprise Me slider too
            _surpriseUsePrompts = false;
            StateHasChanged();
        }

        private Task Generate() => resultsGallery?.GenerateImageWithProgress() ?? Task.CompletedTask;

        // Txt2Img "Generate" routing: A/B compare runs both sets; otherwise the normal gallery.
        private Task RunTxt2Img() => _abEnabled ? (abPanel?.GenerateAb() ?? Task.CompletedTask) : Generate();

        private bool Txt2ImgBusy => _abEnabled ? (abPanel?.Busy ?? false) : (resultsGallery?._isGenerating ?? false);

        private async Task OnAbToggled(bool on)
        {
            _abEnabled = on;
            if (!on) _abActive = 0;
            try { await JS.InvokeVoidAsync("sdHaptic.buzz", 12); } catch { }
            StateHasChanged();
        }

        private void SetAbActive(int which)
        {
            _abActive = which;
            StateHasChanged();
        }

        // Restore a recorded generation's prompt + core settings into the active Txt2Img set and jump
        // there. Mirrors how presets apply — in-place mutation + StateHasChanged refreshes the form.
        private void ApplyHistoryToTxt2Img(HistoryEntry e)
        {
            var r = CurrentRequest;
            r.prompt = e.Prompt;
            r.negative_prompt = e.Negative;
            r.seed = e.Seed;
            r.steps = e.Steps;
            r.cfg_scale = e.Cfg;
            if (!string.IsNullOrWhiteSpace(e.Sampler)) r.sampler_name = e.Sampler;
            if (!string.IsNullOrWhiteSpace(e.Scheduler)) r.scheduler = e.Scheduler;
            if (e.Width > 0) r.width = e.Width;
            if (e.Height > 0) r.height = e.Height;
            _activeTabIndex = TabTxt2Img;
            Snackbar.Add("Settings restored to Txt2Img.", Severity.Success);
            StateHasChanged();
        }

        // Copy the currently-edited set onto the other one (in place, to preserve bindings).
        private async Task CopyAbActiveToOther()
        {
            if (_abActive == 0) { _requestB.CopyFrom(_request); CopyCnUnits(Workspace.ControlNetUnitsA, Workspace.ControlNetUnitsB); }
            else { _request.CopyFrom(_requestB); CopyCnUnits(Workspace.ControlNetUnitsB, Workspace.ControlNetUnitsA); }
            try { await JS.InvokeVoidAsync("sdHaptic.buzz", 15); } catch { }
            StateHasChanged();
        }

        // Replace `dst` with clones of `src` so the two A/B sets don't share unit instances.
        private static void CopyCnUnits(List<ControlNetUnit> src, List<ControlNetUnit> dst)
        {
            dst.Clear();
            dst.AddRange(src.Select(u => u.Clone()));
        }

        // "Surprise Me" lottery: compose a random prompt (favourites first, then popular random
        // tags), maybe sprinkle a random LoRA, roll a fresh seed, and generate. Reuses the existing
        // tag/LoRA/seed systems rather than anything bespoke.
        // "Surprise Me": generate from a freshly-composed random prompt (favourites + popular tags,
        // a random LoRA by chance) on a random seed — but feed it to the generator as an OVERRIDE so
        // the user's own prompt/negative/seed inputs are never overwritten.
        private async Task SurpriseMe()
        {
            if (resultsGallery is null or { _isGenerating: true }) return;

            var rng = new Random();
            double w = Math.Clamp(_surpriseCraziness / (double)WorkspaceState.SurpriseCrazinessMax, 0, 1);
            bool tame = w < 0.5; // keep the readable scaffolding only at lower craziness

            // More words the higher the slider: ~4 at 0 up to ~30 at max.
            int posCount = (int)Math.Round(4 + w * 26);

            var tags = new List<string>();
            if (tame)
            {
                tags.Add(rng.Next(2) == 0 ? "1girl" : "1boy");
                // Favourites carry the user's taste — lead with a couple if they have any.
                var favs = _tagService.GetFavorites().ToList();
                if (favs.Count > 0)
                    tags.AddRange(favs.OrderBy(_ => rng.Next()).Take(rng.Next(1, 3)));
            }

            tags.AddRange(_tagService.GetRandomTags(posCount, rng, w));

            if (tame)
            {
                tags.Add("masterpiece");
                tags.Add("best quality");
            }

            var body = string.Join(", ", tags
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(PromptTags.Escape));

            // LoRA chance climbs with craziness (~40% tame → 100% at max).
            string loraPrefix = "";
            if (rng.NextDouble() < 0.4 + w * 0.6 && _tagService.GetRandomLora(rng) is { } lora)
                loraPrefix = $"<lora:{lora}:{(rng.Next(6, 11) / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}>, ";

            var surprisePrompt = loraPrefix + body;

            // Negative: curated cleanup terms when tame; truly random dictionary words when wild.
            string surpriseNegative;
            if (tame)
            {
                var negPool = new[]
                {
                    "worst quality", "low quality", "lowres", "bad anatomy", "bad hands", "missing fingers",
                    "extra digits", "blurry", "jpeg artifacts", "watermark", "signature", "text",
                    "deformed", "bad proportions", "cropped", "out of frame"
                };
                surpriseNegative = string.Join(", ", negPool.OrderBy(_ => rng.Next()).Take(rng.Next(4, 8)));
            }
            else
            {
                int negCount = (int)Math.Round(4 + w * 16);
                surpriseNegative = string.Join(", ", _tagService.GetRandomTags(negCount, rng, w).Select(PromptTags.Escape));
            }

            // Optionally keep the user's own prompts and add the random tags on top of them.
            if (_surpriseUsePrompts)
            {
                surprisePrompt = MergePrompt(_request.prompt, surprisePrompt);
                surpriseNegative = MergePrompt(_request.negative_prompt, surpriseNegative);
            }

            try { await JS.InvokeVoidAsync("sdHaptic.buzz", 20); } catch { }

            // Random prompt + negative + seed, all passed as overrides → the UI's inputs are untouched.
            await resultsGallery.GenerateImageWithProgress(seedOverride: -1, promptOverride: surprisePrompt, negativeOverride: surpriseNegative);
        }

        // Combine the user's own prompt (kept first) with the surprise-generated tags.
        private static string MergePrompt(string? userPart, string surprisePart)
        {
            var user = (userPart ?? "").Trim().TrimEnd(',').Trim();
            if (user.Length == 0) return surprisePart;
            if (string.IsNullOrWhiteSpace(surprisePart)) return user;
            return $"{user}, {surprisePart}";
        }

        private void GenerationEnded(bool generationDone)
        {
            StateHasChanged();
        }

        // --- Tag groups (reusable blocks of tags appended to the prompt) ---

        private void LoadPresets()
        {
            try
            {
                var path = Path.Combine(Directory.GetCurrentDirectory(), "presets.json");
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    Presets = JsonSerializer.Deserialize<List<GenerationPreset>>(json) ?? new List<GenerationPreset>();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading presets: {ex.Message}");
            }
        }

        private void SavePresets()
        {
            try
            {
                var path = Path.Combine(Directory.GetCurrentDirectory(), "presets.json");
                var json = JsonSerializer.Serialize(Presets, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving presets: {ex.Message}");
            }
        }

        private Task OnPresetChanged(string newPresetName)
        {
            _selectedPresetName = newPresetName;
            if (string.IsNullOrEmpty(newPresetName)) return Task.CompletedTask;

            var preset = Presets.FirstOrDefault(p => p.Name == newPresetName);
            if (preset == null) return Task.CompletedTask;

            // Note: presets intentionally do NOT switch the checkpoint — loading a model is a slow
            // operation, so the currently loaded model is kept.

            // Apply to the currently-edited Txt2Img set (set A, or set B when A/B is on that side).
            var r = CurrentRequest;
            r.prompt = preset.Prompt;
            r.negative_prompt = preset.NegativePrompt;
            r.steps = preset.Steps;
            r.sampler_name = preset.SamplerName;
            r.scheduler = preset.Scheduler;
            r.cfg_scale = preset.CfgScale;
            r.cfg_rescale = preset.CfgRescale;
            r.width = preset.Width;
            r.height = preset.Height;
            r.seed = preset.Seed;
            r.enable_hr = preset.EnableHr;
            r.hr_upscaler = preset.HrUpscaler;
            r.hr_scale = preset.HrScale;
            r.hr_resize_x = preset.HrResizeX;
            r.hr_resize_y = preset.HrResizeY;
            r.hr_second_pass_steps = preset.HrSecondPassSteps;
            r.denoising_strength = preset.DenoisingStrength;
            r.hr_cfg = preset.HrCfg;
            r.enable_refiner = preset.EnableRefiner;
            r.refiner_checkpoint = preset.RefinerCheckpoint;
            r.refiner_switch_at = preset.RefinerSwitchAt;

            // ControlNet units → the same set's units, then mirror onto the request.
            var cn = CurrentCnUnits;
            cn.Clear();
            cn.AddRange((preset.ControlNetUnits ?? new()).Select(u => u.Clone()));
            r.alwayson_scripts = ControlNetUnit.BuildAlwaysOn(cn.ToArray());

            // Apply to Img2ImgRequest if the panel exists
            if (img2imgPanel != null)
            {
                img2imgPanel.ApplyPreset(preset);
            }

            StateHasChanged();
            return Task.CompletedTask;
        }

        private async Task SavePresetDialog()
        {
            var options = new DialogOptions { CloseOnEscapeKey = true, MaxWidth = MaxWidth.Small, FullWidth = true };
            string presetName = _selectedPresetName ?? "";

            while (true)
            {
                var parameters = new DialogParameters<SavePresetDialog> { { x => x.PresetName, presetName } };
                var dialog = await DialogService.ShowAsync<SavePresetDialog>("Save Preset", parameters, options);
                var result = await dialog.Result;

                if (result == null || result.Canceled || result.Data is not string typed || string.IsNullOrWhiteSpace(typed))
                {
                    return; // user cancelled the save dialog
                }

                presetName = typed.Trim();
                var existing = Presets.FirstOrDefault(p => p.Name.Equals(presetName, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    // Name clashes with an existing preset: ask what to do.
                    bool? overwrite = await DialogService.ShowMessageBox(
                        "Preset already exists",
                        $"A preset named \"{existing.Name}\" already exists. Do you want to overwrite it?",
                        yesText: "Overwrite", noText: "Change name", cancelText: "Cancel");

                    if (overwrite == null) return;       // Cancel: abandon the save
                    if (overwrite == false) continue;    // Change name: reopen with the name prefilled
                    // overwrite == true: fall through and replace the existing preset
                }

                GenerationPreset newPreset = (_activeTabIndex == TabImg2Img && img2imgPanel != null)
                    ? img2imgPanel.ToPreset(presetName, _selectedModel)
                    : Txt2ImgToPreset(presetName, _selectedModel);

                if (existing != null)
                {
                    Presets.Remove(existing);
                }
                Presets.Add(newPreset);
                SavePresets();
                _selectedPresetName = presetName;
                StateHasChanged();
                return;
            }
        }

        private async Task OpenManagePresetsDialog()
        {
            var parameters = new DialogParameters<ManagePresetsDialog>();
            parameters.Add(x => x.Presets, Presets);
            parameters.Add(x => x.OnPresetsUpdated, EventCallback.Factory.Create(this, SavePresets));

            var options = new DialogOptions { CloseOnEscapeKey = true, MaxWidth = MaxWidth.Medium, FullWidth = true };
            var dialog = await DialogService.ShowAsync<ManagePresetsDialog>("Manage Presets", parameters, options);
            await dialog.Result;
            
            // Check if our currently selected preset still exists, if not clear it
            if (!string.IsNullOrEmpty(_selectedPresetName) && !Presets.Any(p => p.Name == _selectedPresetName))
            {
                _selectedPresetName = "";
            }
            StateHasChanged();
        }

        // Mobile bottom-nav tab switch, with a light haptic tick.
        // Mobile bottom-nav tab switch. The bottom nav sits above dialog overlays, so a dialog can
        // be open when it's tapped — close any open dialog FIRST, then swap tabs.
        private async Task SelectTab(int index)
        {
            if (_activeTabIndex == index) return;
            try { await JS.InvokeVoidAsync("sdCloseDialogs"); } catch { }
            _activeTabIndex = index;
            StateHasChanged();
        }

        // Top tab bar (MudTabs) switch.
        private async Task OnTabChanged(int index)
        {
            if (_activeTabIndex == index) return;
            _activeTabIndex = index;
            try { await JS.InvokeVoidAsync("sdCloseDialogs"); } catch { }
            StateHasChanged();
        }

        // Routes the mobile Generate FAB to whichever tab is active. Tabs without a generate action
        // (LoRA/Gallery/Civitai/Server) do nothing — previously they fell through to txt2img, which is
        // why the FAB on the Upscale tab started a txt2img run instead of upscaling.
        private async Task TriggerGenerate()
        {
            if (_activeTabIndex == TabImg2Img)
            {
                if (img2imgPanel != null) await img2imgPanel.GenerateImg2Img();
            }
            else if (_activeTabIndex == TabUpscale)
            {
                if (upscaleTab != null) await upscaleTab.RunUpscale();
            }
            else if (_activeTabIndex == TabTxt2Img)
            {
                await RunTxt2Img();
            }
        }

        // The mobile FAB only exists on tabs that have a generate action.
        private bool HasGenerateAction =>
            _activeTabIndex is TabTxt2Img or TabImg2Img or TabUpscale;

        // ...and it's disabled exactly when that tab's own action button would be (busy, no input, etc.),
        // plus whenever the SD server is unreachable — nothing can generate then.
        private bool GenerateDisabled =>
            offline || _activeTabIndex switch
            {
                TabTxt2Img => Txt2ImgBusy,
                TabImg2Img => img2imgPanel is null || !img2imgPanel.HasInitImage || img2imgPanel.IsGenerating,
                TabUpscale => upscaleTab is null || !upscaleTab.CanGenerate,
                _ => true
            };

        private GenerationPreset Txt2ImgToPreset(string name, string checkpoint)
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
                Seed = _request.seed,
                EnableHr = _request.enable_hr,
                HrUpscaler = _request.hr_upscaler,
                HrScale = _request.hr_scale,
                HrResizeX = _request.hr_resize_x,
                HrResizeY = _request.hr_resize_y,
                HrSecondPassSteps = _request.hr_second_pass_steps,
                DenoisingStrength = _request.denoising_strength,
                HrCfg = _request.hr_cfg,
                EnableRefiner = _request.enable_refiner,
                RefinerCheckpoint = _request.refiner_checkpoint,
                RefinerSwitchAt = _request.refiner_switch_at,
                ControlNetUnits = Workspace.ControlNetUnitsA.Select(u => u.Clone()).ToList()
            };
        }

    }
}
