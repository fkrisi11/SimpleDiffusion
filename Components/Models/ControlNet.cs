namespace SimpleDiffusion.Components.Models
{
    /// <summary>
    /// One ControlNet unit: the UI state plus the args object sent to the reForge/A1111 ControlNet API
    /// under <c>alwayson_scripts.controlnet.args[]</c>.
    /// </summary>
    public class ControlNetUnit
    {
        public bool Enabled { get; set; } = true;
        public string? ImageB64 { get; set; }            // raw base64 control image (no data: prefix)
        public string ControlType { get; set; } = "All"; // UI grouping (drives the module/model lists)
        public string Module { get; set; } = "none";     // preprocessor
        public string Model { get; set; } = "None";
        public double Weight { get; set; } = 1.0;
        public double GuidanceStart { get; set; } = 0.0;
        public double GuidanceEnd { get; set; } = 1.0;
        public int ControlMode { get; set; } = 0;         // 0 Balanced, 1 prompt-priority, 2 control-priority
        public int ResizeMode { get; set; } = 1;          // 0 Just Resize, 1 Crop and Resize, 2 Resize and Fill
        public int ProcessorRes { get; set; } = 512;
        public int ThresholdA { get; set; } = 64;
        public int ThresholdB { get; set; } = 64;
        public bool PixelPerfect { get; set; } = false;
        public bool LowVram { get; set; } = false;

        /// <summary>img2img only: feed the painted inpaint mask to this unit (for inpaint ControlNets).</summary>
        public bool UsePaintedMask { get; set; } = false;

        /// <summary>Transient mask base64, injected at generation time when <see cref="UsePaintedMask"/>
        /// is set. Not part of the saved/serialized state.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string? MaskB64 { get; set; }

        /// <summary>A unit only counts if it's enabled and actually has a control image to work from.</summary>
        public bool IsActive => Enabled && !string.IsNullOrEmpty(ImageB64);

        /// <summary>The single-unit args object the ControlNet API expects. When a mask is present the
        /// image is sent as <c>{image, mask}</c> (how the inpaint ControlNet wants its masked region).</summary>
        public object ToArg() => new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["image"] = MaskB64 != null ? new { image = ImageB64, mask = MaskB64 } : (object?)ImageB64,
            ["module"] = Module,
            ["model"] = Model,
            ["weight"] = Weight,
            ["resize_mode"] = ResizeMode,
            ["low_vram"] = LowVram,
            ["processor_res"] = ProcessorRes,
            ["threshold_a"] = ThresholdA,
            ["threshold_b"] = ThresholdB,
            ["guidance_start"] = GuidanceStart,
            ["guidance_end"] = GuidanceEnd,
            ["pixel_perfect"] = PixelPerfect,
            ["control_mode"] = ControlMode,
        };

        /// <summary>Deep-ish copy for presets / A-B copy. The transient mask is intentionally dropped.</summary>
        public ControlNetUnit Clone()
        {
            var copy = (ControlNetUnit)MemberwiseClone();
            copy.MaskB64 = null;
            return copy;
        }

        /// <summary>
        /// Build the <c>alwayson_scripts</c> object for a request from the active units, or null when
        /// none are active (so the request serializes exactly as before — no ControlNet key).
        /// </summary>
        public static object? BuildAlwaysOn(params ControlNetUnit[] units)
        {
            var args = units.Where(u => u.IsActive).Select(u => u.ToArg()).ToList();
            return args.Count == 0 ? null : new { controlnet = new { args } };
        }
    }

    /// <summary>Short, friendly explanations per ControlNet control type, matched loosely on the
    /// type name (the backend's type keys vary by build). Returns a generic primer when unknown.</summary>
    public static class ControlNetHelp
    {
        public static string For(string? controlType)
        {
            var t = (controlType ?? "").ToLowerInvariant();
            if (t == "all")
                return "<b>All</b> shows every installed preprocessor and model with no task filtering — handy for browsing everything or building a combination the grouped types don't list. If you're unsure, pick a specific control type (Canny, Depth, Pose…) instead and the preprocessor + model are matched for you.";
            if (t.Contains("instant"))
                return "<b>InstantID</b> transfers a person's facial identity from a reference photo onto your generation — keep the same face across new scenes and styles. Needs the InstantID model(s) and a face encoder; works best with a clear, front-facing reference.";
            if (t.Contains("photomaker"))
                return "<b>PhotoMaker</b> generates images of a specific person from one or more reference photos, driven by your prompt (e.g. \"a photo of <i>person</i> as an astronaut\"). Needs the PhotoMaker model; reference the subject in the prompt with its trigger word.";
            if (t.Contains("ip-adapter") || t.Contains("ipadapter"))
                return "<b>IP-Adapter</b> uses your image as a visual prompt — it transfers the subject/style of the reference. Needs an IP-Adapter model and a matching CLIP encoder.";
            if (t.Contains("revision"))
                return "<b>Revision</b> (SDXL) uses one or more reference images as a visual prompt — it conditions on their overall content/style rather than edges or pose. Works with or without a text prompt.";
            if (t.Contains("t2i"))
                return "<b>T2I-Adapter</b> is a lighter-weight alternative to ControlNet for edge / depth / pose / sketch conditioning.";
            if (t.Contains("canny"))
                return "<b>Canny</b> finds hard edges and locks the result to that outline — best for keeping the exact composition while restyling.<br/><br/><i>Tip:</i> adjust the Low/High thresholds (Advanced) to capture more or fewer edges.";
            if (t.Contains("depth"))
                return "<b>Depth</b> estimates how near/far things are and preserves the 3D layout and perspective. Great for relighting or restyling a scene while keeping its structure.";
            if (t.Contains("normal"))
                return "<b>Normal map</b> captures fine surface relief and orientation — preserves bumps and small detail more than depth.";
            if (t.Contains("pose"))
                return "<b>OpenPose</b> detects body (and optionally hand/face) keypoints, so you can place a character in a specific pose. Use a reference photo of the pose you want.";
            if (t.Contains("mlsd"))
                return "<b>MLSD</b> detects straight lines — best for architecture, interiors and other geometric scenes.";
            if (t.Contains("lineart") || t.Contains("line"))
                return "<b>Lineart</b> extracts clean line art to colour or render. Match the preprocessor to your source (realistic vs anime); use the <i>invert</i> preprocessor for black-on-white scans, or <i>none</i> if your image is already line art.";
            if (t.Contains("soft") || t.Contains("hed") || t.Contains("edge"))
                return "<b>Soft edge</b> detects edges softly (gentler than Canny) — preserves forms and silhouettes with a more natural feel.";
            if (t.Contains("scribble"))
                return "<b>Scribble</b> turns rough scribbles into images, following your loose lines with lots of creative freedom.";
            if (t.Contains("sketch"))
                return "<b>Sketch</b> turns a rough drawing into a finished image, following your lines while filling in detail and colour. Similar to Scribble — give it a simple line drawing and describe what it should become.";
            if (t.Contains("seg"))
                return "<b>Segmentation</b> controls layout by labelled regions (sky, building, person…). Good for composing a scene by area.";
            if (t.Contains("tile"))
                return "<b>Tile</b> preserves and adds detail and lets you regenerate at higher resolution coherently — excellent for upscaling and detail passes. Tolerant of a roughly-matching control image.";
            if (t.Contains("blur"))
                return "<b>Blur</b> guides generation from a blurred version of the image — useful for sharpening, restoring, or regenerating detail while keeping the overall content. Often paired with Tile for high-res detail passes.";
            if (t.Contains("inpaint"))
                return "<b>Inpaint</b> regenerates a masked region with strong coherence to the surroundings. On img2img, enable <i>Use init image</i> and <i>Use painted inpaint mask</i> to feed it your painted area.";
            if (t.Contains("reference"))
                return "<b>Reference</b> needs no model — it transfers style/content from your reference image (reference_only / reference_adain).";
            if (t.Contains("shuffle"))
                return "<b>Shuffle</b> scrambles the reference's content/colours for a loose style-transfer effect.";
            if (t.Contains("recolor"))
                return "<b>Recolor</b> adds colour to a grayscale image while keeping its structure.";
            if (t.Contains("p2p") || t.Contains("instruct"))
                return "<b>Instruct-Pix2Pix</b> applies prompt-described edits to the reference image.";
            return "Pick a <b>control type</b>, a <b>preprocessor</b> (extracts the control signal from your image), and a matching <b>model</b>. Control weight sets how strongly it steers; the Start/End steps limit when control applies (a lower End lets the model free-run the later steps).";
        }
    }

    /// <summary>A page (Txt2Img / Img2Img) and its live ControlNet unit list — passed to the
    /// "Send to ControlNet" picker so the user can target existing units or add a new one per page.</summary>
    public class CnSendTarget
    {
        public string Page { get; set; } = "";
        public List<ControlNetUnit> Units { get; set; } = new();
    }

    /// <summary>The picker's result: which existing units should receive the image, and which pages
    /// should get a brand-new unit holding it.</summary>
    public class CnSendResult
    {
        public List<ControlNetUnit> Units { get; set; } = new();
        public List<CnSendTarget> AddTo { get; set; } = new();
        public bool Any => Units.Count > 0 || AddTo.Count > 0;
    }

    // --- ControlNet API discovery DTOs ---

    public class ControlTypeInfo
    {
        public List<string> module_list { get; set; } = new();
        public List<string> model_list { get; set; } = new();
        public string default_option { get; set; } = "none";
        public string default_model { get; set; } = "None";
    }

    public class ControlTypesResponse
    {
        public Dictionary<string, ControlTypeInfo> control_types { get; set; } = new();
    }

    // module_list also returns per-preprocessor slider config (labels, ranges, defaults) — used to
    // render accurate Advanced sliders. The sliders array is positional: [0] resolution, [1] threshold_a,
    // [2] threshold_b, with nulls for slots a preprocessor doesn't use.
    public class CnSlider
    {
        public string? name { get; set; }
        public double value { get; set; }
        public double min { get; set; }
        public double max { get; set; }
        public double step { get; set; } = 1;
    }

    public class CnModuleDetail
    {
        public bool model_free { get; set; }
        public List<CnSlider?> sliders { get; set; } = new();
    }

    public class CnModuleListResponse
    {
        public List<string> module_list { get; set; } = new();
        public Dictionary<string, CnModuleDetail>? module_detail { get; set; }
    }

    public class CnModelListResponse { public List<string> model_list { get; set; } = new(); }
    public class CnDetectResponse { public List<string> images { get; set; } = new(); }
}
