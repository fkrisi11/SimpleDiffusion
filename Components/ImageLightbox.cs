using MudBlazor;

namespace SimpleDiffusion.Components;

/// <summary>
/// Opens the fullscreen <see cref="ImageDialog"/> viewer. Centralises the dialog parameters/options
/// that were copy-pasted across the gallery, results, upscale, LoRA and ControlNet surfaces.
/// Caller-specific logic (NSFW guards, building the URL list, adjusting the index) stays at the call
/// site — only the identical parameters/options/Show tail lives here.
/// </summary>
public static class ImageLightbox
{
    /// <param name="captions">Optional per-image labels, parallel to <paramref name="images"/>.
    /// Only the upscale comparison uses these.</param>
    /// <param name="closeOnBackdropClick">Whether clicking the backdrop also dismisses the viewer
    /// (the gallery and LoRA viewers opt in). Esc / mobile Back always close, regardless.</param>
    public static Task<IDialogReference> ShowAsync(
        IDialogService dialogService,
        IReadOnlyList<string> images,
        int index = 0,
        IReadOnlyList<string>? captions = null,
        bool closeOnBackdropClick = false)
    {
        var parameters = new DialogParameters<ImageDialog>
        {
            { x => x.AllImages, images.ToList() },
            { x => x.SelectedIndex, Math.Clamp(index, 0, Math.Max(0, images.Count - 1)) },
        };
        if (captions is { Count: > 0 })
            parameters.Add(x => x.Captions, captions.ToList());

        var options = new DialogOptions
        {
            MaxWidth = MaxWidth.Large,
            FullWidth = true,
            NoHeader = true,
            CloseButton = true,
            // Always closeable by Esc — and therefore by the mobile Back button, which sdBackClose
            // turns into an Esc keypress on the topmost dialog. The MudDialogProvider default is
            // false, so this must be set explicitly for every viewer to be uniformly dismissable.
            CloseOnEscapeKey = true,
            BackdropClick = closeOnBackdropClick ? true : (bool?)null,
        };

        return dialogService.ShowAsync<ImageDialog>("", parameters, options);
    }
}
