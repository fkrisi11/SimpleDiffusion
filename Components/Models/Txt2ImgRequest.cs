using System.Text.Json;

namespace SimpleDiffusion.Components.Models
{
    public class Txt2ImgRequest
    {
        /// <summary>Largest accepted seed value (2^32 - 1), matching the Stable Diffusion WebUI cap.</summary>
        public const long SeedMax = 4294967295;

        public string prompt { get; set; } = "";
        public string negative_prompt { get; set; } = "";
        public int steps { get; set; } = 20;
        public string sampler_name { get; set; }
        public string scheduler { get; set; } = "Automatic";
        public double cfg_scale { get; set; } = 7.0;
        public double cfg_rescale { get; set; } = 0.0;
        public int width { get; set; } = 768;
        public int height { get; set; } = 512;
        public long seed { get; set; } = -1;

        // Variation ("variation seed") parameters. subseed_strength 0 = disabled, so these are
        // harmless on every normal request. subseed -1 lets the server pick a fresh one per image.
        public long subseed { get; set; } = -1;
        public double subseed_strength { get; set; } = 0.0;

        public int n_iter { get; set; } = 1;
        public int batch_size { get; set; } = 1;

        // Hires. fix parameters
        public bool enable_hr { get; set; } = false;
        public string hr_upscaler { get; set; } = "Latent";
        public double hr_scale { get; set; } = 2.0;
        public int hr_resize_x { get; set; } = 0; // "Resize width to"
        public int hr_resize_y { get; set; } = 0; // "Resize height to"
        public int hr_second_pass_steps { get; set; } = 0; // "Hires steps"
        public double denoising_strength { get; set; } = 0.7;
        public double hr_cfg { get; set; } = 0; // "Hires CFG Scale"
        public bool enable_refiner { get; set; } = false;
        public string? refiner_checkpoint { get; set; } = null;
        public double refiner_switch_at { get; set; } = 0.8;

        // ControlNet (and any other alwayson scripts). Null = omitted from the JSON, so a request with
        // no ControlNet serializes exactly as before. Built from the active units before each generation.
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public object? alwayson_scripts { get; set; }

        /// <summary>Copy every field from <paramref name="other"/> into this instance in place
        /// (keeps the reference, so existing data-bindings keep working). Used by A/B "copy set".</summary>
        public void CopyFrom(Txt2ImgRequest other)
        {
            prompt = other.prompt;
            negative_prompt = other.negative_prompt;
            steps = other.steps;
            sampler_name = other.sampler_name;
            scheduler = other.scheduler;
            cfg_scale = other.cfg_scale;
            cfg_rescale = other.cfg_rescale;
            width = other.width;
            height = other.height;
            seed = other.seed;
            subseed = other.subseed;
            subseed_strength = other.subseed_strength;
            n_iter = other.n_iter;
            batch_size = other.batch_size;
            enable_hr = other.enable_hr;
            hr_upscaler = other.hr_upscaler;
            hr_scale = other.hr_scale;
            hr_resize_x = other.hr_resize_x;
            hr_resize_y = other.hr_resize_y;
            hr_second_pass_steps = other.hr_second_pass_steps;
            denoising_strength = other.denoising_strength;
            hr_cfg = other.hr_cfg;
            enable_refiner = other.enable_refiner;
            refiner_checkpoint = other.refiner_checkpoint;
            refiner_switch_at = other.refiner_switch_at;
            alwayson_scripts = other.alwayson_scripts;
        }

        public Txt2ImgRequest Clone()
        {
            return new Txt2ImgRequest
            {
                prompt = prompt,
                negative_prompt = negative_prompt,
                steps = steps,
                sampler_name = sampler_name,
                scheduler = scheduler,
                cfg_scale = cfg_scale,
                cfg_rescale = cfg_rescale,
                width = width,
                height = height,
                seed = seed,
                subseed = subseed,
                subseed_strength = subseed_strength,
                n_iter = n_iter,
                batch_size = batch_size,
                enable_hr = enable_hr,
                hr_upscaler = hr_upscaler,
                hr_scale = hr_scale,
                hr_resize_x = hr_resize_x,
                hr_resize_y = hr_resize_y,
                hr_second_pass_steps = hr_second_pass_steps,
                denoising_strength = denoising_strength,
                hr_cfg = hr_cfg,
                enable_refiner = enable_refiner,
                refiner_checkpoint = refiner_checkpoint,
                refiner_switch_at = refiner_switch_at,
                alwayson_scripts = alwayson_scripts,
            };
        }
    }

    public class Txt2ImgResponse
    {
        public List<string> images { get; set; } = new(); // Base64 strings
        public string info { get; set; }
    }

    public class SdProgressResponse
    {
        public double progress { get; set; } // 0.0 to 1.0
        public double eta_relative { get; set; }
        public string current_image { get; set; } // Base64 preview
        public SdProgressState? state { get; set; }
    }

    public sealed class SdProgressState
    {
        public int job_no { get; set; }     // 0-based index within the current batch
        public int job_count { get; set; }  // total jobs in the current batch
    }

    public class LoraModel
    {
        public string name { get; set; }
        public string alias { get; set; }
        public string path { get; set; }
        public JsonElement metadata { get; set; }
        public string? previewPath { get; set; }
        public int nsfwLevel { get; set; }
        public string? type { get; set; }
        public string? triggerWords { get; set; }
        public string? baseModel { get; set; }

        // What sidecar info exists on disk (for the "missing info" filters).
        public bool hasPreview { get; set; }
        public bool hasJson { get; set; }
        public bool hasTxt { get; set; }
        public bool hasHtml { get; set; }
        public DateTime modified { get; set; }
        public long sizeBytes { get; set; }
    }

    /// <summary>A LoRA chosen from the browser: its name plus (optional) trigger words to insert.</summary>
    public sealed record LoraSelection(string Name, string? Triggers);

    public sealed class LoraMetaDto
    {
        public string? SidecarText { get; set; }
        public string? HtmlPage { get; set; }

        public string? CivitaiName { get; set; }
        public string? Creator { get; set; }
        public string? BaseModel { get; set; }
        public string? VersionName { get; set; }
        public string? DownloadUrl { get; set; }

        public int NsfwLevel { get; set; }

        public List<string>? Tags { get; set; }
        public List<string>? TrainedWords { get; set; }
    }
}
