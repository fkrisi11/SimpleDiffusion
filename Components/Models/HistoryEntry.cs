namespace SimpleDiffusion.Components.Models;

/// <summary>One recorded generation: enough parameters to recreate it, plus the result-store ids of
/// the images it produced. Stored per device in the browser's localStorage so it survives a page
/// refresh; the images themselves live in the server result store and are recoverable only while they
/// remain cached (the store is wiped on app restart and pruned when it fills).</summary>
public sealed class HistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>Where it came from: "Txt2Img", "Img2Img", "A/B A", "A/B B", …</summary>
    public string Source { get; set; } = "Txt2Img";

    public string Prompt { get; set; } = "";
    public string Negative { get; set; } = "";
    public long Seed { get; set; } = -1;
    public int Steps { get; set; }
    public double Cfg { get; set; }
    public string? Sampler { get; set; }
    public string? Scheduler { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double? Denoise { get; set; }   // hires / img2img denoising strength, when relevant

    /// <summary>Result-store ids of the produced images (served via <c>/results/file?id=…</c>).</summary>
    public List<string> ResultIds { get; set; } = new();

    /// <summary>For a masked img2img run: result-store id of the source (init) image, saved alongside
    /// the outputs so it can be reviewed/downloaded from the History tab.</summary>
    public string? SourceId { get; set; }

    /// <summary>Masks saved for a masked run: a single B&amp;W mask for inpaint-mask/outpaint, or the
    /// individual sketch layers plus a combined overlay for inpaint-sketch. Each carries a label.</summary>
    public List<MaskRef> Masks { get; set; } = new();

    /// <summary>Snapshot a txt2img request into a history entry. Other modes build entries inline
    /// (their request types differ), so this only covers the <see cref="Txt2ImgRequest"/> shape.</summary>
    public static HistoryEntry FromTxt2Img(Txt2ImgRequest r, List<string> ids, string source = "Txt2Img") => new()
    {
        Source = source,
        Prompt = r.prompt,
        Negative = r.negative_prompt,
        Seed = r.seed,
        Steps = r.steps,
        Cfg = r.cfg_scale,
        Sampler = r.sampler_name,
        Scheduler = r.scheduler,
        Width = r.width,
        Height = r.height,
        Denoise = r.enable_hr ? r.denoising_strength : null,
        ResultIds = ids,
    };
}

/// <summary>One saved mask image (result-store id) with a display label, e.g. a sketch layer name or
/// "Combined".</summary>
public sealed class MaskRef
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "Mask";
}
