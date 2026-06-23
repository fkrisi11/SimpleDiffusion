using System.Net.Http.Json;
using MudBlazor;

namespace SimpleDiffusion.Components;

/// <summary>Shared "Save to Gallery" flow used by the txt2img + img2img result galleries:
/// asks which album to save into (when any albums exist), then POSTs the PNG.</summary>
public static class GallerySaveFlow
{
    public static async Task RunAsync(IDialogService dialogs, HttpClient http, ISnackbar snackbar, string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return;

        List<string> albums;
        try { albums = await http.GetFromJsonAsync<List<string>>("/gallery/albums") ?? new(); }
        catch { albums = new(); }

        string? album = null;
        if (albums.Count > 0)
        {
            var parameters = new DialogParameters<GalleryAlbumPickerDialog> { { x => x.Albums, albums } };
            var options = new DialogOptions { CloseOnEscapeKey = true, MaxWidth = MaxWidth.ExtraSmall, FullWidth = true };
            var dlg = await dialogs.ShowAsync<GalleryAlbumPickerDialog>("Save to album", parameters, options);
            var res = await dlg.Result;
            if (res is null || res.Canceled) return; // user backed out
            album = res.Data as string;              // null = Unsorted (root)
        }

        try
        {
            var resp = await http.PostAsJsonAsync("/gallery/save", new { album, base64 });
            snackbar.Add(resp.IsSuccessStatusCode ? "Saved to gallery." : "Failed to save to gallery.",
                         resp.IsSuccessStatusCode ? Severity.Success : Severity.Error);
        }
        catch (Exception ex) { snackbar.Add("Save failed: " + ex.Message, Severity.Error); }
    }
}
