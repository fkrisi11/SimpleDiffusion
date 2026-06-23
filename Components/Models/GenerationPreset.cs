namespace SimpleDiffusion.Components.Models
{
    public class GenerationPreset
    {
        public string Name { get; set; } = "";
        public string Checkpoint { get; set; } = "";

        // Prompts
        public string Prompt { get; set; } = "";
        public string NegativePrompt { get; set; } = "";

        // Core settings
        public int Steps { get; set; } = 20;
        public string SamplerName { get; set; } = "DPM++ 2M";
        public string Scheduler { get; set; } = "Automatic";
        public double CfgScale { get; set; } = 7.0;
        public double CfgRescale { get; set; } = 0.0;
        public int Width { get; set; } = 768;
        public int Height { get; set; } = 512;
        public long Seed { get; set; } = -1;

        // Hires. fix
        public bool EnableHr { get; set; } = false;
        public string HrUpscaler { get; set; } = "Latent";
        public double HrScale { get; set; } = 2.0;
        public int HrResizeX { get; set; } = 0;
        public int HrResizeY { get; set; } = 0;
        public int HrSecondPassSteps { get; set; } = 0;
        public double DenoisingStrength { get; set; } = 0.7;
        public double HrCfg { get; set; } = 0;

        // Refiner
        public bool EnableRefiner { get; set; } = false;
        public string? RefinerCheckpoint { get; set; } = null;
        public double RefinerSwitchAt { get; set; } = 0.8;

        // ControlNet units (incl. their control images). Empty for presets saved before this existed.
        public List<ControlNetUnit> ControlNetUnits { get; set; } = new();
    }
}
