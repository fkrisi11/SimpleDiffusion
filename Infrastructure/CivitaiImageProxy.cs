using System.Diagnostics;
using System.Net;

namespace SimpleDiffusion.Infrastructure;

/// <summary>
/// Proxies Civitai CDN images through our own origin. Loading them directly from
/// &lt;img src="https://image.civitai.com/..."&gt; can fail (hotlink / referer / mixed
/// content on LAN HTTP), so we fetch server-side and stream the bytes back.
/// </summary>
public static class CivitaiImageProxy
{
    /// <summary>
    /// Opens a file's containing folder in Explorer (selecting the file). Only honoured for
    /// loopback connections — i.e. the browser is on the same machine as the server — since
    /// it launches a process on the host.
    /// </summary>
    public static void MapCivitaiOpenFolder(this WebApplication app)
    {
        app.MapPost("/civitai-open-folder", (string path, HttpContext ctx) =>
        {
            var ip = ctx.Connection.RemoteIpAddress;
            if (ip is null || !IPAddress.IsLoopback(ip))
                return Results.Forbid();

            if (!OperatingSystem.IsWindows())
                return Results.Problem("Only supported on Windows.");

            try
            {
                if (File.Exists(path))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                else if (Directory.Exists(path))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                else
                    return Results.NotFound();

                return Results.Ok();
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });
    }

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("SimpleDiffusion/1.0");
        c.DefaultRequestHeaders.Referrer = new Uri("https://civitai.com/");
        return c;
    }

    /// <summary>
    /// Lists sub-directories of a path (or drives when no path) for the in-app folder picker.
    /// Loopback-only: it exposes the host's filesystem, so only the host machine may browse it.
    /// </summary>
    public static void MapFolderBrowser(this WebApplication app)
    {
        app.MapGet("/civitai-list-dirs", (string? path, string? pattern, HttpContext ctx) =>
        {
            var ip = ctx.Connection.RemoteIpAddress;
            if (ip is null || !IPAddress.IsLoopback(ip))
                return Results.Forbid();

            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    var drives = DriveInfo.GetDrives()
                        .Where(d => d.IsReady)
                        .Select(d => d.RootDirectory.FullName)
                        .ToList();
                    return Results.Json(new { current = "", parent = (string?)null, dirs = drives, files = new List<string>() });
                }

                var dir = new DirectoryInfo(path);
                if (!dir.Exists) return Results.NotFound();

                var subs = dir.GetDirectories()
                    .Where(d => (d.Attributes & FileAttributes.Hidden) == 0 && (d.Attributes & FileAttributes.System) == 0)
                    .Select(d => d.FullName)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // When a pattern is supplied (e.g. "*.bat") the picker is in file mode: also list
                // matching files in this folder so the user can pick one.
                var files = new List<string>();
                if (!string.IsNullOrWhiteSpace(pattern))
                {
                    files = dir.GetFiles(pattern)
                        .Where(f => (f.Attributes & FileAttributes.Hidden) == 0 && (f.Attributes & FileAttributes.System) == 0)
                        .Select(f => f.FullName)
                        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                return Results.Json(new { current = dir.FullName, parent = dir.Parent?.FullName, dirs = subs, files });
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });
    }

    public static void MapCivitaiImageProxy(this WebApplication app)
    {
        app.MapGet("/civitai-image", async (string url, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return Results.BadRequest("Invalid url.");

            // Only allow Civitai's image CDN — don't turn this into an open proxy.
            if (!uri.Host.EndsWith("civitai.com", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest("Host not allowed.");

            try
            {
                using var resp = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
                if (!resp.IsSuccessStatusCode)
                    return Results.StatusCode((int)resp.StatusCode);

                var contentType = resp.Content.Headers.ContentType?.ToString() ?? "image/jpeg";

                // Read fully before returning: the response (and its stream) is disposed
                // when this handler returns, so we can't hand a live stream to Results.Stream.
                var bytes = await resp.Content.ReadAsByteArrayAsync(ctx.RequestAborted);

                // Cache aggressively — these CDN assets are immutable.
                ctx.Response.Headers.CacheControl = "public, max-age=86400";
                return Results.Bytes(bytes, contentType);
            }
            catch (OperationCanceledException)
            {
                return Results.Empty;
            }
            catch
            {
                return Results.StatusCode(502);
            }
        });
    }
}
