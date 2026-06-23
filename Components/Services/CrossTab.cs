namespace SimpleDiffusion.Components.Services;

/// <summary>
/// Tiny per-circuit message bus for cross-tab actions, e.g. jumping from the Civitai browser
/// to a downloaded model in the LoRA browser.
/// </summary>
public sealed class CrossTab
{
    /// <summary>Raised with a search query (model file base name) to locate in the LoRA browser.</summary>
    public event Action<string>? JumpToLoraRequested;

    public void RequestJumpToLora(string query) => JumpToLoraRequested?.Invoke(query);

    /// <summary>Raised with a base64 PNG to load as the Img2Img init image (and switch to that tab).</summary>
    public event Action<string>? SendToImg2ImgRequested;

    public void RequestSendToImg2Img(string base64Png) => SendToImg2ImgRequested?.Invoke(base64Png);

    /// <summary>Raised with a base64 PNG to queue on the Upscale tab (and switch to it).</summary>
    public event Action<string>? SendToUpscaleRequested;

    public void RequestSendToUpscale(string base64Png) => SendToUpscaleRequested?.Invoke(base64Png);

    /// <summary>Raised with a base64 image to add as a new ControlNet unit on Img2Img (and switch to it).</summary>
    public event Action<string>? SendToControlNetRequested;

    public void RequestSendToControlNet(string base64) => SendToControlNetRequested?.Invoke(base64);

    /// <summary>Generate variations of an existing image: same prompt + base seed, but a fresh
    /// subseed per image at the given strength. Raised by the fullscreen viewer, handled on Home.</summary>
    public event Action<VariationsRequest>? VariationsRequested;

    public void RequestVariations(VariationsRequest req) => VariationsRequested?.Invoke(req);
}

/// <summary>A request to seed-vary an image. Prompt/Negative come from the source image's metadata
/// (already resolved, so wildcards don't re-roll); other settings stay as the current txt2img form.
/// OriginalBase64 is the source image (raw base64) so it can be kept in the results gallery.</summary>
public sealed record VariationsRequest(long Seed, string Prompt, string Negative, double Strength, int Count, string? OriginalBase64);
