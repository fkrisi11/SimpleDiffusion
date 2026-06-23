namespace SimpleDiffusion.Infrastructure;

public sealed class LoraMediaItemDto
{
    public string Path { get; set; } = "";     // full path on server
    public string Url { get; set; } = "";      // url you can put into <img>/<video>
    public string Kind { get; set; } = "";     // "image" or "video"
    public string ContentType { get; set; } = "";
}

public sealed class LoraPreviewListDto
{
    public string LoraPath { get; set; } = "";
    public string BaseName { get; set; } = "";
    public string Directory { get; set; } = "";

    public List<LoraPreviewItemDto> Items { get; set; } = new();
}

public sealed class LoraPreviewItemDto
{
    public string FilePath { get; set; } = "";   // full server path
    public string FileName { get; set; } = "";   // just the name
    public string Extension { get; set; } = "";  // ".png", ".webm"
    public string Kind { get; set; } = "";       // "image" | "video"
    public string Mime { get; set; } = "";       // "image/png" | "video/mp4"
    public long SizeBytes { get; set; }          // optional
    public string Url { get; set; } = "";        // a safe URL to fetch it
}

/// <summary>Returned by the LoRA details dialog to request reopening it for a different version.</summary>
public sealed class LoraVersionSwitch
{
    public string Path { get; set; } = "";
}