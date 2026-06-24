namespace SimpleDiffusion.Components.Models
{
    public class SdModel
    {
        public string Title { get; set; }      // Display name (e.g., "v1-5-pruned.safetensors")
        public string Model_Name { get; set; } // Internal name
        public string Hash { get; set; }       // Short hash
        public string Sha256 { get; set; }     // Full hash
        public string Filename { get; set; }   // Full local path
    }

    /// <summary>One installed VAE from <c>sdapi/v1/sd-vae</c>. The dropdown shows <see cref="model_name"/>;
    /// "Automatic"/"None" are added by the UI (they aren't returned by the API).</summary>
    public class SdVaeItem
    {
        public string model_name { get; set; } = "";
        public string? filename { get; set; }
    }

    public class UpscalerInfo
    {
        public string name { get; set; } = "";
        public string? model_name { get; set; }
        public string? model_path { get; set; }
        public string? model_url { get; set; }
    }

    public class SchedulerInfo
    {
        public string name { get; set; } = "";     // Internal ID (e.g., "karras")
        public string label { get; set; } = "";    // Display Name (e.g., "Karras")
                                                   // Some versions also return "description" or "default_sampling_steps"
    }
}
