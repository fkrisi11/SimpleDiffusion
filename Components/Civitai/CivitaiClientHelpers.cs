using Microsoft.AspNetCore.Components;

namespace SimpleDiffusion.Components.Civitai;

public static class CivitaiClientHelpers
{
    /// <summary>
    /// True when the browser is on the same machine as the server (accessed via localhost),
    /// so host-only actions like "open folder" make sense to offer.
    /// </summary>
    public static bool IsLocalClient(NavigationManager nav)
    {
        try
        {
            var host = new Uri(nav.BaseUri).Host;
            return host is "localhost" or "127.0.0.1" or "::1";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Ask the server (which must be the local host) to reveal a path in Explorer.</summary>
    public static async Task OpenFolderAsync(HttpClient http, string path)
    {
        try
        {
            await http.PostAsync($"/civitai-open-folder?path={Uri.EscapeDataString(path)}", null);
        }
        catch { /* best effort — button only shows for local clients */ }
    }
}
