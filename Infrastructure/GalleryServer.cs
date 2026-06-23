using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

namespace SimpleDiffusion.Infrastructure;

// DTOs returned to the Gallery tab.
public sealed class GalleryItemDto
{
    public string File { get; set; } = "";
    public string? Album { get; set; }   // null = unsorted (gallery root)
    public string Url { get; set; } = "";
    public long Size { get; set; }
    public DateTime Modified { get; set; }
}

public sealed class GalleryAlbumDto
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
}

public sealed class GalleryOverviewDto
{
    public List<GalleryAlbumDto> Albums { get; set; } = new();
    public List<GalleryItemDto> Unsorted { get; set; } = new(); // PNGs at the gallery root
}

/// <summary>
/// A shared, on-disk image gallery stored in a <c>gallery/</c> folder beside the app. Albums are
/// immediate sub-folders; "unsorted" images are PNGs at the root (not an album you navigate into).
/// Images are stored verbatim (we never re-encode), so the embedded generation parameters survive.
/// </summary>
public static class GalleryServer
{
    private static string Root => Path.Combine(Directory.GetCurrentDirectory(), "gallery");

    // One reusable name for "save the file as" — sortable, unique-ish, .png only.
    private static string NewFileName() => $"SD_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";

    /// <summary>A single safe path segment (album or file name) — rejects separators / traversal.</summary>
    private static string? SafeSegment(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.Contains('/') || s.Contains('\\') || s.Contains("..")) return null;
        if (s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        return s;
    }

    private static string AlbumDir(string? album)
    {
        Directory.CreateDirectory(Root);
        if (string.IsNullOrEmpty(album)) return Root;
        return Path.Combine(Root, album);
    }

    private static GalleryItemDto ToItem(FileInfo fi, string? album) => new()
    {
        File = fi.Name,
        Album = album,
        Url = $"/gallery/file?file={Uri.EscapeDataString(fi.Name)}" + (album is null ? "" : $"&album={Uri.EscapeDataString(album)}"),
        Size = fi.Length,
        Modified = fi.LastWriteTime
    };

    // Cache of each image's embedded "parameters" text, keyed by path, invalidated by mtime — so a
    // prompt search only reads each file once. We read just a prefix: A1111 writes the params tEXt
    // chunk right after IHDR (before the image data), so the start of the file is enough.
    private static readonly ConcurrentDictionary<string, (DateTime Mtime, string Text)> _metaSearchCache = new();
    private const int MetaScanBytes = 256 * 1024;

    private static string ParamsForSearch(FileInfo fi)
    {
        try
        {
            if (_metaSearchCache.TryGetValue(fi.FullName, out var c) && c.Mtime == fi.LastWriteTimeUtc)
                return c.Text;

            byte[] buf;
            using (var fs = fi.OpenRead())
            {
                int n = (int)Math.Min(MetaScanBytes, fs.Length);
                buf = new byte[n];
                fs.ReadExactly(buf, 0, n);
            }
            var text = SimpleDiffusion.Components.PngMetadata.ExtractParameters(buf) ?? "";
            _metaSearchCache[fi.FullName] = (fi.LastWriteTimeUtc, text);
            return text;
        }
        catch { return ""; }
    }

    private static void SearchDir(DirectoryInfo dir, string? album, string term, List<GalleryItemDto> outList)
    {
        if (!dir.Exists) return;
        foreach (var fi in dir.EnumerateFiles("*.png"))
        {
            if (fi.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || ParamsForSearch(fi).Contains(term, StringComparison.OrdinalIgnoreCase))
                outList.Add(ToItem(fi, album));
        }
    }

    /// <summary>Remove the derived display tiers for a stored image (across the _root / album cache
    /// folders) — called when its source is deleted or moved so orphan tiers don't linger.</summary>
    private static void DeleteTiersFor(string? album, string file)
    {
        try
        {
            var cacheDir = Path.Combine(TierCacheRoot, album ?? "_root");
            if (!Directory.Exists(cacheDir)) return;
            foreach (var f in Directory.EnumerateFiles(cacheDir, Path.GetFileNameWithoutExtension(file) + ".*"))
            {
                try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    private static List<GalleryItemDto> ListImages(string dir, string? album)
    {
        if (!Directory.Exists(dir)) return new();
        return new DirectoryInfo(dir).EnumerateFiles("*.png")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => ToItem(f, album))
            .ToList();
    }

    // One-shot in-memory zip downloads. The image bytes already live on the server, so we build the
    // archive here and hand the browser a short-lived URL — rather than shipping base64 over the
    // circuit just to re-zip it client-side. Each entry is removed on first fetch.
    private static readonly ConcurrentDictionary<Guid, byte[]> PendingZips = new();

    /// <summary>Builds a zip from (entry name, base64-or-data-URL) pairs and returns a one-shot
    /// download URL. A leading "data:...;base64," prefix is stripped; duplicate names are suffixed.</summary>
    public static string BuildZipDownload(IEnumerable<(string Name, string Base64)> entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int i = 0;
            foreach (var (name, b64) in entries)
            {
                i++;
                var raw = b64 ?? "";
                var comma = raw.IndexOf(',');
                if (raw.StartsWith("data:", StringComparison.Ordinal) && comma >= 0) raw = raw[(comma + 1)..];

                byte[] bytes;
                try { bytes = Convert.FromBase64String(raw); } catch { continue; }

                var entryName = string.IsNullOrWhiteSpace(name) ? $"image_{i}.png" : name;
                if (!used.Add(entryName))   // de-dupe so the archive stays valid
                {
                    entryName = $"{Path.GetFileNameWithoutExtension(entryName)}_{i}{Path.GetExtension(entryName)}";
                    used.Add(entryName);
                }

                var entry = zip.CreateEntry(entryName, System.IO.Compression.CompressionLevel.Fastest);
                using var es = entry.Open();
                es.Write(bytes, 0, bytes.Length);
            }
        }

        var id = Guid.NewGuid();
        PendingZips[id] = ms.ToArray();
        return $"/gallery/zip/{id}";
    }

    /// <summary>The Content-Disposition download name from a <c>?dl=</c> value: a bare filename
    /// (any path segments stripped), or null when this isn't a download request.</summary>
    private static string? DownloadName(string? dl)
        => string.IsNullOrWhiteSpace(dl) ? null : Path.GetFileName(dl);

    public static void MapGallery(this WebApplication app)
    {
        // One-shot download for a server-built zip (see BuildZipDownload); removed on first fetch.
        app.MapGet("/gallery/zip/{id:guid}", (Guid id, string? dl) =>
            PendingZips.TryRemove(id, out var bytes)
                ? Results.File(bytes, "application/zip", DownloadName(dl) ?? "images.zip")
                : Results.NotFound());

        // Root view: albums (with counts) on top, unsorted images below.
        app.MapGet("/gallery/overview", () =>
        {
            Directory.CreateDirectory(Root);
            var albums = new DirectoryInfo(Root).EnumerateDirectories()
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(d => new GalleryAlbumDto { Name = d.Name, Count = d.EnumerateFiles("*.png").Count() })
                .ToList();

            return Results.Json(new GalleryOverviewDto { Albums = albums, Unsorted = ListImages(Root, null) });
        });

        // Names only (used by the save picker).
        app.MapGet("/gallery/albums", () =>
        {
            Directory.CreateDirectory(Root);
            var names = new DirectoryInfo(Root).EnumerateDirectories()
                .Select(d => d.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return Results.Json(names);
        });

        // Images inside one album.
        app.MapGet("/gallery/album", (string name) =>
        {
            var a = SafeSegment(name);
            if (a is null) return Results.BadRequest("Invalid album.");
            return Results.Json(ListImages(AlbumDir(a), a));
        });

        // Search every stored image (unsorted + all albums) by filename or embedded prompt/seed text.
        app.MapGet("/gallery/search", (string q) =>
        {
            var term = (q ?? "").Trim();
            if (term.Length == 0) return Results.Json(new List<GalleryItemDto>());

            Directory.CreateDirectory(Root);
            var results = new List<GalleryItemDto>();
            SearchDir(new DirectoryInfo(Root), null, term, results);
            foreach (var d in new DirectoryInfo(Root).EnumerateDirectories())
                SearchDir(d, d.Name, term, results);

            return Results.Json(results.OrderByDescending(r => r.Modified).ToList());
        });

        // Stream a stored PNG. When the viewer asks for an optimized display tier (w=<longEdge>),
        // serve a cached downscaled JPEG instead — but only when the source is actually larger than
        // the cap. The original PNG on disk is never modified.
        app.MapGet("/gallery/file", async (string file, string? album, int? w, string? fmt, string? dl) =>
        {
            var f = SafeSegment(file);
            var a = album is null ? null : SafeSegment(album);
            if (f is null || (album is not null && a is null)) return Results.BadRequest();
            var path = Path.Combine(AlbumDir(a), f);
            if (!File.Exists(path)) return Results.NotFound();

            // When dl is set this is a download (not an inline <img>): attach a Content-Disposition
            // filename so browsers that ignore the JS download attribute still save the real name.
            var dlName = DownloadName(dl);

            // fmt=jpg: a cached full-resolution JPG (for "download as JPG"). w wins if both are given.
            if (string.Equals(fmt, "jpg", StringComparison.OrdinalIgnoreCase) && w is not { } )
            {
                var jpg = await GetOrCreateFullJpegAsync(path, a, f);
                if (jpg is not null) return Results.File(jpg, "image/jpeg", dlName, enableRangeProcessing: true);
            }
            if (w is { } maxEdge && maxEdge > 0)
            {
                var tier = await GetOrCreateFitTierAsync(path, a, f, maxEdge);
                if (tier is not null) return Results.File(tier, "image/jpeg", enableRangeProcessing: true);
            }
            return Results.File(path, "image/png", dlName, enableRangeProcessing: true);
        });

        // Serve a downscaled tier for an in-memory (base64) image that was prepared server-side by
        // CreateMemTierAsync. The heavy decode/resize already happened on the server; the client just
        // downloads this small JPEG instead of receiving the multi-MB base64 over SignalR.
        app.MapGet("/gallery/memtier", (string id, int w) =>
        {
            if (string.IsNullOrEmpty(id) || !id.All(char.IsAsciiHexDigit)) return Results.BadRequest();
            w = Math.Clamp(w, 256, MaxTierEdge);
            var path = Path.Combine(MemTierDir, $"{id}.{w}q{TierQuality}.jpg");
            if (!File.Exists(path)) return Results.NotFound();
            return Results.File(path, "image/jpeg", enableRangeProcessing: true);
        });

        // Serve a session result (upscale/generation output) stored on disk by ResultStore, by id.
        // With ?w it returns a downscaled JPEG tier (reusing the same pipeline as /gallery/file) so
        // the grid/viewer never receive the full multi-thousand-pixel image; without it, the original.
        app.MapGet("/results/file", async (string id, int? w, string? fmt, string? dl) =>
        {
            var path = ResultPath(id);
            if (path is null) return Results.NotFound();
            var dlName = DownloadName(dl);
            if (string.Equals(fmt, "jpg", StringComparison.OrdinalIgnoreCase) && w is not { })
            {
                var jpg = await GetOrCreateFullJpegAsync(path, "_results", id + ".png");
                if (jpg is not null) return Results.File(jpg, "image/jpeg", dlName, enableRangeProcessing: true);
            }
            if (w is { } maxEdge && maxEdge > 0)
            {
                var tier = await GetOrCreateFitTierAsync(path, "_results", id + ".png", maxEdge);
                if (tier is not null) return Results.File(tier, "image/jpeg", enableRangeProcessing: true);
            }
            return Results.File(path, "image/png", dlName, enableRangeProcessing: true);
        });

        // Embedded A1111 generation parameters for one stored image (empty if none).
        app.MapGet("/gallery/metadata", (string file, string? album) =>
        {
            var f = SafeSegment(file);
            var a = album is null ? null : SafeSegment(album);
            if (f is null || (album is not null && a is null)) return Results.BadRequest();
            var path = Path.Combine(AlbumDir(a), f);
            if (!File.Exists(path)) return Results.NotFound();

            string? text = null, upscaling = null, generated = null;
            try
            {
                var bytes = File.ReadAllBytes(path);
                text = SimpleDiffusion.Components.PngMetadata.ExtractParameters(bytes);
                upscaling = SimpleDiffusion.Components.PngMetadata.ExtractText(bytes, "Upscaling");
                // Prefer the generation time; fall back to the (save-time) Creation Time for older images.
                generated = SimpleDiffusion.Components.PngMetadata.ExtractText(bytes, "Generation Time")
                         ?? SimpleDiffusion.Components.PngMetadata.ExtractText(bytes, "Creation Time");
            }
            catch { }
            return Results.Json(new { text = text ?? "", upscaling = upscaling ?? "", generated = generated ?? "" });
        });

        // Save a base64 PNG into an album (or the root when album is null/empty).
        app.MapPost("/gallery/save", async (HttpContext ctx) =>
        {
            // A 16k PNG's base64 can be hundreds of MB — well past Kestrel's 30MB default body cap.
            // This is a local single-user app, so lift the limit for this one (image-bearing) endpoint.
            var sizeFeature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = null;

            var body = await ctx.Request.ReadFromJsonAsync<SaveRequest>();
            if (body is null || string.IsNullOrWhiteSpace(body.Base64)) return Results.BadRequest("No image.");

            var a = string.IsNullOrEmpty(body.Album) ? null : SafeSegment(body.Album);
            if (body.Album is { Length: > 0 } && a is null) return Results.BadRequest("Invalid album.");

            var b64 = body.Base64;
            var comma = b64.IndexOf(',');
            if (b64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0) b64 = b64[(comma + 1)..];

            byte[] bytes;
            try { bytes = Convert.FromBase64String(b64); }
            catch { return Results.BadRequest("Bad base64."); }

            // Stamp the save time into a new tEXt chunk (existing chunks/params are left intact).
            try { bytes = SimpleDiffusion.Components.PngMetadata.AddTextChunk(bytes, "Creation Time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")); }
            catch { }

            var dir = AlbumDir(a);
            Directory.CreateDirectory(dir);
            var name = NewFileName();
            await File.WriteAllBytesAsync(Path.Combine(dir, name), bytes);
            return Results.Json(new { file = name, album = a });
        });

        app.MapPost("/gallery/album/create", (string name) =>
        {
            var a = SafeSegment(name);
            if (a is null) return Results.BadRequest("Invalid name.");
            var dir = AlbumDir(a);
            if (Directory.Exists(dir)) return Results.Conflict("Album already exists.");
            Directory.CreateDirectory(dir);
            return Results.Ok();
        });

        app.MapPost("/gallery/album/rename", (string oldName, string newName) =>
        {
            var o = SafeSegment(oldName);
            var n = SafeSegment(newName);
            if (o is null || n is null) return Results.BadRequest("Invalid name.");
            var src = AlbumDir(o);
            var dst = AlbumDir(n);
            if (!Directory.Exists(src)) return Results.NotFound();
            if (Directory.Exists(dst)) return Results.Conflict("Target album exists.");
            Directory.Move(src, dst);
            // Old album's tiers no longer map to anything; they regenerate under the new name on demand.
            try { var cd = Path.Combine(TierCacheRoot, o); if (Directory.Exists(cd)) Directory.Delete(cd, recursive: true); } catch { }
            return Results.Ok();
        });

        // Delete an album. deleteFiles=false moves its PNGs to the root (they become unsorted).
        app.MapPost("/gallery/album/delete", (string name, bool deleteFiles) =>
        {
            var a = SafeSegment(name);
            if (a is null) return Results.BadRequest("Invalid name.");
            var dir = AlbumDir(a);
            if (!Directory.Exists(dir)) return Results.NotFound();

            if (!deleteFiles)
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.png"))
                {
                    var target = Path.Combine(Root, Path.GetFileName(f));
                    target = UniquePath(target);
                    File.Move(f, target);
                }
            }
            Directory.Delete(dir, recursive: true);
            try { var cd = Path.Combine(TierCacheRoot, a); if (Directory.Exists(cd)) Directory.Delete(cd, recursive: true); } catch { }
            return Results.Ok();
        });

        // Move one image between albums / root.
        app.MapPost("/gallery/move", async (HttpContext ctx) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<MoveRequest>();
            if (body is null) return Results.BadRequest();
            var f = SafeSegment(body.File);
            var from = string.IsNullOrEmpty(body.FromAlbum) ? null : SafeSegment(body.FromAlbum);
            var to = string.IsNullOrEmpty(body.ToAlbum) ? null : SafeSegment(body.ToAlbum);
            if (f is null || (body.FromAlbum is { Length: > 0 } && from is null) || (body.ToAlbum is { Length: > 0 } && to is null))
                return Results.BadRequest();

            var src = Path.Combine(AlbumDir(from), f);
            if (!File.Exists(src)) return Results.NotFound();
            var dstDir = AlbumDir(to);
            Directory.CreateDirectory(dstDir);
            var dst = UniquePath(Path.Combine(dstDir, f));
            File.Move(src, dst);
            DeleteTiersFor(from, f); // old-location tiers are stale; the new location regenerates lazily
            return Results.Json(new { file = Path.GetFileName(dst), album = to });
        });

        app.MapPost("/gallery/image/delete", (string file, string? album) =>
        {
            var f = SafeSegment(file);
            var a = album is null ? null : SafeSegment(album);
            if (f is null || (album is not null && a is null)) return Results.BadRequest();
            var path = Path.Combine(AlbumDir(a), f);
            if (!File.Exists(path)) return Results.NotFound();
            File.Delete(path);
            DeleteTiersFor(a, f); // drop the orphaned display tiers
            return Results.Ok();
        });
    }

    // Derived display tiers live OUTSIDE the gallery folder so they're never listed as albums.
    private static string TierCacheRoot => Path.Combine(Directory.GetCurrentDirectory(), "gallery-cache");

    // Hard ceiling for any tier — clamps client requests so a bad value can't ask for a gigantic
    // re-encode. The fit view requests a device-sized width (≤2048); the on-zoom detail tier (phase 2)
    // requests up to this, gated by device capability on the client.
    private const int MaxTierEdge = 4096;

    // JPEG quality for display tiers. The fit view is downscaled and not pixel-inspected (zoom will
    // fetch full-res in phase 2), so q85 + progressive keeps files small and fast over wifi. Encoded
    // into the cache filename so changing it transparently invalidates older cached tiers.
    private const int TierQuality = 85;

    // Quality for full-resolution JPG downloads — high (visually lossless), 4:4:4, since the user is
    // saving the file, not just viewing it. Cached on disk and reused on subsequent downloads.
    private const int DownloadJpegQuality = 92;

    // Decoding a huge PNG is memory- and CPU-heavy, so only ever run ONE conversion at a time.
    // This stops two large opens from stacking and doubling peak memory.
    private static readonly SemaphoreSlim _tierLock = new(1, 1);

    // Total size cap for the derived-tier cache (file + in-memory tiers). When exceeded we evict the
    // oldest files — they're cheap to regenerate on demand — so the cache stays bounded without ever
    // wiping reusable tiers on each generation. Evicting also retires orphans (deleted-source tiers).
    private const long TierCacheMaxBytes = 500L * 1024 * 1024;

    /// <summary>Startup housekeeping: drop the ephemeral in-memory-image tiers (their source images
    /// don't survive a restart), then bound the rest of the cache to its size cap. Call once at boot.</summary>
    public static void CleanTierCacheOnStartup()
    {
        try { if (Directory.Exists(MemTierDir)) Directory.Delete(MemTierDir, recursive: true); } catch { }
        PruneTierCache();
    }

    /// <summary>Evict oldest-written tier files until the cache is back under its size cap. Cheap and
    /// safe to call after each write (always under <see cref="_tierLock"/>, so never races itself).</summary>
    private static void PruneTierCache()
    {
        try
        {
            if (!Directory.Exists(TierCacheRoot)) return;
            var files = new DirectoryInfo(TierCacheRoot).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
            long total = files.Sum(f => f.Length);
            if (total <= TierCacheMaxBytes) return;

            foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                try { total -= f.Length; f.Delete(); } catch { }
                if (total <= TierCacheMaxBytes) break;
            }
        }
        catch { }
    }

    /// <summary>Return the path to a cached downscaled JPEG of <paramref name="srcPath"/> no larger
    /// than <paramref name="maxEdge"/> on its long side, generating it on first use. Returns null
    /// when the source is already small enough (caller should serve the original) or on any error.</summary>
    private static async Task<string?> GetOrCreateFitTierAsync(string srcPath, string? album, string file, int maxEdge)
    {
        try
        {
            maxEdge = Math.Clamp(maxEdge, 256, MaxTierEdge);

            var src = new FileInfo(srcPath);
            var cacheDir = Path.Combine(TierCacheRoot, album ?? "_root");
            Directory.CreateDirectory(cacheDir);
            var cachePath = Path.Combine(cacheDir, $"{Path.GetFileNameWithoutExtension(file)}.{maxEdge}q{TierQuality}.jpg");

            // Reuse a cache entry that's at least as new as the source (fast path, no lock).
            if (IsFresh(cachePath, src)) return cachePath;

            await _tierLock.WaitAsync();
            try
            {
                // Another request may have just built it while we waited for the lock.
                if (IsFresh(cachePath, src)) return cachePath;

                // Cheap header probe — skip a pointless lossy re-encode of images already within the cap.
                int sw, sh;
                using (var hdr = NetVips.Image.NewFromFile(srcPath, access: NetVips.Enums.Access.Sequential))
                {
                    sw = hdr.Width;
                    sh = hdr.Height;
                }
                if (sw <= maxEdge && sh <= maxEdge) return null;

                var tmp = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";

                // libvips thumbnail: fits within maxEdge x maxEdge preserving aspect, never upscales,
                // and decodes natively (orders of magnitude faster + lower memory than managed decode).
                using (var thumb = NetVips.Image.Thumbnail(srcPath, maxEdge, height: maxEdge, size: NetVips.Enums.Size.Down))
                {
                    // Progressive (interlaced) JPEG renders top-to-bottom as it arrives, so it appears
                    // sooner on mobile; q85 with default (auto) chroma subsampling keeps the file small.
                    thumb.Jpegsave(tmp, q: TierQuality, interlace: true);
                }

                // Move into place so a concurrent request never reads a partial file.
                File.Move(tmp, cachePath, overwrite: true);
                PruneTierCache();
                return cachePath;
            }
            finally { _tierLock.Release(); }
        }
        catch { return null; }
    }

    private static bool IsFresh(string cachePath, FileInfo src) =>
        File.Exists(cachePath) && File.GetLastWriteTimeUtc(cachePath) >= src.LastWriteTimeUtc;

    /// <summary>Return the path to a cached full-resolution JPG of <paramref name="srcPath"/>,
    /// converting it once via libvips (fast, low-memory, no canvas size limits) and reusing the cached
    /// file thereafter. For "download as JPG". Returns null on error.</summary>
    private static async Task<string?> GetOrCreateFullJpegAsync(string srcPath, string? album, string file)
    {
        try
        {
            var src = new FileInfo(srcPath);
            var cacheDir = Path.Combine(TierCacheRoot, album ?? "_root");
            Directory.CreateDirectory(cacheDir);
            var cachePath = Path.Combine(cacheDir, $"{Path.GetFileNameWithoutExtension(file)}.fullq{DownloadJpegQuality}.jpg");

            if (IsFresh(cachePath, src)) return cachePath; // already on disk — just serve it

            await _tierLock.WaitAsync();
            try
            {
                if (IsFresh(cachePath, src)) return cachePath;

                var tmp = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var img = NetVips.Image.NewFromFile(srcPath, access: NetVips.Enums.Access.Sequential))
                {
                    img.Jpegsave(tmp, q: DownloadJpegQuality, subsampleMode: NetVips.Enums.ForeignSubsample.Off);
                }
                File.Move(tmp, cachePath, overwrite: true);
                PruneTierCache();
                return cachePath;
            }
            finally { _tierLock.Release(); }
        }
        catch { return null; }
    }

    // ---- Ephemeral result store (#5): upscale/generation outputs live on disk, referenced by id,
    // so the full (up to 16k) base64 is neither held in server memory nor shipped to the client.
    // Originals are the source for the on-the-fly display tiers; cleared on startup (new session). ----
    private static string ResultsDir => Path.Combine(Directory.GetCurrentDirectory(), "results-cache");

    // Cap on the result store. Originals are the source for tiers/save/download, so unlike tiers they
    // can't be cheaply regenerated — but a long session of huge (16k) outputs shouldn't fill the disk.
    // We evict the oldest when over the cap; only ancient results in a marathon session are affected.
    /// <summary>Result-store size cap in bytes (oldest results are evicted past this). Defaults to
    /// 2 GB; overridable from settings (<c>MaxResultCacheMB</c>) at startup.</summary>
    public static long MaxResultBytes { get; set; } = 2L * 1024 * 1024 * 1024; // 2 GB

    /// <summary>Wipe the result store — call once at boot; last session's outputs are gone.</summary>
    public static void ClearResultsOnStartup()
    {
        try { if (Directory.Exists(ResultsDir)) Directory.Delete(ResultsDir, recursive: true); } catch { }
    }

    /// <summary>Persist a base64 (or data-URL) image to the result store and return its id.</summary>
    public static string SaveResult(string base64OrDataUrl)
    {
        var b64 = base64OrDataUrl;
        var comma = b64.IndexOf(',');
        if (b64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0) b64 = b64[(comma + 1)..];
        return SaveResultBytes(Convert.FromBase64String(b64));
    }

    /// <summary>Persist raw PNG bytes to the result store and return its id — for callers that have
    /// already decoded/modified the bytes (e.g. to embed PNG metadata) and don't want a re-encode.</summary>
    public static string SaveResultBytes(byte[] bytes)
    {
        // Stamp when this image was produced, so the viewer can show a generation timestamp. It's
        // embedded in the PNG, so it survives the verbatim copy into the gallery (where the separate
        // "Creation Time" stamp records the later save time).
        try { bytes = SimpleDiffusion.Components.PngMetadata.AddTextChunk(bytes, "Generation Time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")); }
        catch { }
        Directory.CreateDirectory(ResultsDir);
        var id = Guid.NewGuid().ToString("N");
        File.WriteAllBytes(Path.Combine(ResultsDir, id + ".png"), bytes);
        PruneResultStore();
        return id;
    }

    /// <summary>Evict the oldest result originals until the store is back under its size cap.</summary>
    private static void PruneResultStore()
    {
        try
        {
            if (!Directory.Exists(ResultsDir)) return;
            var files = new DirectoryInfo(ResultsDir).EnumerateFiles("*.png").ToList();
            long total = files.Sum(f => f.Length);
            if (total <= MaxResultBytes) return;

            foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                try { total -= f.Length; f.Delete(); } catch { }
                if (total <= MaxResultBytes) break;
            }
        }
        catch { }
    }

    /// <summary>Persist a batch of base64 outputs to the result store, returning their ids (skipping
    /// any that fail to decode). Shared by the result tabs.</summary>
    public static List<string> SaveResults(IEnumerable<string> base64Images)
    {
        var ids = new List<string>();
        foreach (var b in base64Images)
        {
            try { ids.Add(SaveResult(b)); } catch { }
        }
        return ids;
    }

    /// <summary>Path to a stored result, or null if the id is invalid / the file is gone.</summary>
    private static string? ResultPath(string id)
    {
        if (string.IsNullOrEmpty(id) || !id.All(char.IsAsciiHexDigit)) return null;
        var p = Path.Combine(ResultsDir, id + ".png");
        return File.Exists(p) ? p : null;
    }

    /// <summary>Whether a result id is still in the store (i.e. recoverable). Used by the History tab
    /// to tell "image still cached" from "metadata only".</summary>
    public static bool ResultExists(string id) => ResultPath(id) is not null;

    /// <summary>Raw bytes of a stored result, or null if missing — for save/send/download flows.</summary>
    public static async Task<byte[]?> GetResultBytesAsync(string id)
    {
        var p = ResultPath(id);
        return p is null ? null : await File.ReadAllBytesAsync(p);
    }

    /// <summary>A stored result as raw base64, or null if missing.</summary>
    public static async Task<string?> GetResultBase64Async(string id)
    {
        var bytes = await GetResultBytesAsync(id);
        return bytes is null ? null : Convert.ToBase64String(bytes);
    }

    /// <summary>Downscale a live-generation preview to a small JPEG data URL and report whether it's
    /// effectively blank (near-black). Lets the client receive a tiny image each poll tick instead of
    /// the full-resolution base64, and moves the blank check off the client. Returns the original on
    /// failure. Call from a background thread (it decodes/encodes via libvips).</summary>
    public static (bool Blank, string? DataUrl) PreparePreview(string base64OrDataUrl, int maxEdge, bool optimize)
    {
        var b64 = base64OrDataUrl;
        var comma = b64.IndexOf(',');
        if (b64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0) b64 = b64[(comma + 1)..];

        // Optimization off: hand back the full preview unchanged (as a proper data URL — never raw
        // base64, which the browser would try to FETCH as a URL → "URI too long").
        if (!optimize) return (false, "data:image/png;base64," + b64);

        try
        {
            var bytes = Convert.FromBase64String(b64);
            maxEdge = Math.Clamp(maxEdge, 256, MaxTierEdge);
            using var thumb = NetVips.Image.ThumbnailBuffer(bytes, maxEdge, height: maxEdge, size: NetVips.Enums.Size.Down);
            // JPEG can't carry alpha; live previews are often RGBA, so flatten first. Then materialize
            // into memory (CopyMemory) so we can BOTH inspect (Max) and encode (Jpegsave) — reading a
            // sequential PNG loader twice throws "vipspng: out of order read".
            using var img = (thumb.HasAlpha() ? thumb.Flatten() : thumb).CopyMemory();
            if (img.Max() <= 10) return (true, null); // near-black frame — not worth showing yet
            var jpeg = img.JpegsaveBuffer(q: 80);
            return (false, "data:image/jpeg;base64," + Convert.ToBase64String(jpeg));
        }
        catch { return (false, "data:image/png;base64," + b64); } // fall back to the full preview (valid data URL)
    }

    /// <summary>Pre-build a display tier for a stored result so the grid isn't blank while it lazily
    /// generates. Fire-and-forget; safe to call right after <see cref="SaveResult"/>.</summary>
    public static async Task WarmResultTierAsync(string id, int width)
    {
        var p = ResultPath(id);
        if (p is not null) await GetOrCreateFitTierAsync(p, "_results", id + ".png", width);
    }

    // Tiers for in-memory (base64) images, keyed by content hash so the same image reuses its tier.
    private static string MemTierDir => Path.Combine(TierCacheRoot, "_mem");

    /// <summary>Downscale an in-memory base64 image (already in server memory) to a device-sized JPEG
    /// tier and return a <c>/gallery/memtier</c> URL for it. This keeps the heavy decode/resize on the
    /// server so a phone never has to receive/decode the full-size image. Returns null when the image
    /// is already within the cap (caller should just show it directly) or on any error.</summary>
    public static async Task<string?> CreateMemTierAsync(string base64OrDataUrl, int maxEdge)
    {
        try
        {
            maxEdge = Math.Clamp(maxEdge, 256, MaxTierEdge);

            var b64 = base64OrDataUrl;
            var comma = b64.IndexOf(',');
            if (b64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0) b64 = b64[(comma + 1)..];

            byte[] bytes;
            try { bytes = Convert.FromBase64String(b64); }
            catch { return null; }

            // Content-addressed cache key — re-viewing the same image hits the cache.
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(bytes));

            Directory.CreateDirectory(MemTierDir);
            var cachePath = Path.Combine(MemTierDir, $"{hash}.{maxEdge}q{TierQuality}.jpg");
            var url = $"/gallery/memtier?id={hash}&w={maxEdge}";

            if (File.Exists(cachePath)) return url;

            await _tierLock.WaitAsync();
            try
            {
                if (File.Exists(cachePath)) return url;

                // Already within the cap — not worth a tier; let the caller show the image as-is.
                using (var hdr = NetVips.Image.NewFromBuffer(bytes))
                {
                    if (hdr.Width <= maxEdge && hdr.Height <= maxEdge) return null;
                }

                var tmp = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var thumb = NetVips.Image.ThumbnailBuffer(bytes, maxEdge, height: maxEdge, size: NetVips.Enums.Size.Down))
                {
                    thumb.Jpegsave(tmp, q: TierQuality, interlace: true);
                }
                File.Move(tmp, cachePath, overwrite: true);
                PruneTierCache();
                return url;
            }
            finally { _tierLock.Release(); }
        }
        catch { return null; }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name}_{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private sealed class SaveRequest { public string? Album { get; set; } public string Base64 { get; set; } = ""; }
    private sealed class MoveRequest { public string? FromAlbum { get; set; } public string File { get; set; } = ""; public string? ToAlbum { get; set; } }
}
