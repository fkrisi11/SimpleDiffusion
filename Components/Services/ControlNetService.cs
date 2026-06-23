using System.Net.Http.Json;

namespace SimpleDiffusion.Components.Services
{
    /// <summary>
    /// Discovers ControlNet preprocessors/models from the SD backend (reForge) and runs preprocessor
    /// previews. Scoped per circuit; the type map is cached after the first successful fetch so opening
    /// the panel repeatedly doesn't re-hit the API. The SD <see cref="HttpClient"/> is supplied by the
    /// caller (the same client the generation panels already hold) so this service needs no base address.
    /// </summary>
    public class ControlNetService
    {
        private Dictionary<string, ControlTypeInfo>? _types;
        private Dictionary<string, CnModuleDetail>? _moduleDetail;

        /// <summary>True once a successful discovery has happened and at least one model was found.</summary>
        public bool Available { get; private set; }

        /// <summary>Raised when the cache is invalidated so open panels can re-fetch the model list.</summary>
        public event Action? Changed;

        /// <summary>
        /// Drop the cached type map so the next <see cref="GetTypesAsync"/> re-queries the backend —
        /// used after new ControlNet models are downloaded (e.g. the LoRA page's "reload from disk").
        /// </summary>
        public void Invalidate()
        {
            _types = null;
            _moduleDetail = null;
            Available = false;
            Changed?.Invoke();
        }

        /// <summary>
        /// Per-preprocessor slider config (labels, ranges, defaults) from <c>controlnet/module_list</c>,
        /// so the Advanced section can show the *right* sliders (e.g. "Canny Low/High Threshold") instead
        /// of generic ones. Empty when the backend doesn't provide it.
        /// </summary>
        public async Task<Dictionary<string, CnModuleDetail>> GetModuleDetailAsync(HttpClient http)
        {
            if (_moduleDetail != null) return _moduleDetail;
            try
            {
                var resp = await http.GetFromJsonAsync<CnModuleListResponse>("controlnet/module_list");
                _moduleDetail = resp?.module_detail ?? new();
            }
            catch { _moduleDetail = new(); }
            return _moduleDetail;
        }

        /// <summary>
        /// Control types grouped by name (Canny, Depth, OpenPose, …), each with its module/model lists
        /// and defaults. Falls back to a single "All" group built from the flat module/model lists on
        /// backends without <c>/controlnet/control_types</c>. Returns an empty map if the API is absent.
        /// </summary>
        public async Task<Dictionary<string, ControlTypeInfo>> GetTypesAsync(HttpClient http)
        {
            if (_types != null) return _types;

            try
            {
                var resp = await http.GetFromJsonAsync<ControlTypesResponse>("controlnet/control_types");
                if (resp?.control_types is { Count: > 0 } t)
                {
                    Available = true;
                    return _types = t;
                }
            }
            catch { /* fall through to the flat-list fallback */ }

            try
            {
                var mods = await http.GetFromJsonAsync<CnModuleListResponse>("controlnet/module_list");
                var models = await http.GetFromJsonAsync<CnModelListResponse>("controlnet/model_list");
                var modelList = new List<string> { "None" };
                if (models?.model_list != null) modelList.AddRange(models.model_list);
                Available = (models?.model_list?.Count ?? 0) > 0;
                return _types = new Dictionary<string, ControlTypeInfo>
                {
                    ["All"] = new ControlTypeInfo
                    {
                        module_list = mods?.module_list ?? new List<string> { "none" },
                        model_list = modelList
                    }
                };
            }
            catch
            {
                return _types = new Dictionary<string, ControlTypeInfo>();
            }
        }

        /// <summary>
        /// Run a preprocessor on an image and return the resulting control map (raw base64), or null on
        /// failure. Used for the "Preview preprocessor" button so the user can see edges/pose/depth.
        /// </summary>
        public async Task<string?> DetectAsync(HttpClient http, string module, string imageB64, int res, int thresholdA, int thresholdB)
        {
            try
            {
                var body = new
                {
                    controlnet_module = module,
                    controlnet_input_images = new[] { imageB64 },
                    controlnet_processor_res = res,
                    controlnet_threshold_a = thresholdA,
                    controlnet_threshold_b = thresholdB
                };
                var resp = await http.PostAsJsonAsync("controlnet/detect", body);
                resp.EnsureSuccessStatusCode();
                var det = await resp.Content.ReadFromJsonAsync<CnDetectResponse>();
                return det?.images?.FirstOrDefault();
            }
            catch { return null; }
        }
    }
}
