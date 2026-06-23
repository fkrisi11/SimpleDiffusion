using System.Collections.Concurrent;
using System.Text.Json;

namespace SimpleDiffusion.Components.Civitai;

public enum DownloadStatus { Queued, Downloading, Paused, Completed, Failed, Cancelled }

/// <summary>Live state for a single in-flight (or finished) download. Bound directly by the UI.</summary>
public sealed class CivitaiDownload
{
    public required int VersionId { get; init; }
    public required int ModelId { get; init; }
    public required string ModelName { get; init; }
    public required string FileName { get; init; }
    public required string TargetPath { get; init; }

    internal string? ApiKey { get; init; }

    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public long BytesReceived { get; set; }
    public long TotalBytes { get; set; }
    public string? Error { get; set; }

    public double Percent => TotalBytes > 0
        ? Math.Clamp(BytesReceived * 100.0 / TotalBytes, 0, 100)
        : 0;

    public bool CanPause => Status == DownloadStatus.Downloading;
    public bool CanResume => Status is DownloadStatus.Paused or DownloadStatus.Failed;

    // Captured so a paused/failed download can be resumed later.
    internal CivitaiModel Model { get; init; } = default!;
    internal CivitaiModelVersion Version { get; init; } = default!;
    internal string DownloadUrl { get; init; } = "";
    internal bool PauseRequested { get; set; }
    internal CancellationTokenSource Cts { get; set; } = new();
}

/// <summary>
/// Singleton that downloads model files (streamed, with progress) and writes the
/// <c>.civitai.json</c> + preview sidecars in the exact layout the existing local
/// LoRA browser already reads. Raises <see cref="Changed"/> so any open UI can re-render.
/// </summary>
public sealed class CivitaiDownloadManager
{
    private static readonly string[] ImgExts = { ".png", ".jpg", ".jpeg", ".webp", ".gif" };

    private readonly IConfiguration _config;
    private readonly CivitaiService _civitai;
    private readonly string _modelsRoot;

    private readonly ConcurrentDictionary<int, CivitaiDownload> _downloads = new();

    // Insertion-ordered view for the downloads panel (active + this-session history).
    private readonly List<CivitaiDownload> _ordered = new();
    private readonly object _orderLock = new();

    // Adjustable concurrency limit; downloads beyond it wait in the Queued state.
    private readonly object _slotLock = new();
    private int _maxConcurrent = 4;
    private int _activeSlots;
    private readonly Queue<TaskCompletionSource> _slotWaiters = new();

    public int MaxConcurrent => _maxConcurrent;

    /// <summary>Change how many downloads may run at once (1–16); takes effect immediately.</summary>
    public void SetMaxConcurrent(int value)
    {
        value = Math.Clamp(value, 1, 16);
        lock (_slotLock)
        {
            _maxConcurrent = value;
            // If we just raised the limit, wake queued downloads up to the new ceiling.
            while (_activeSlots < _maxConcurrent && _slotWaiters.Count > 0)
            {
                var w = _slotWaiters.Dequeue();
                if (w.TrySetResult()) _activeSlots++;
            }
        }
    }

    private async Task AcquireSlotAsync(CancellationToken ct)
    {
        TaskCompletionSource tcs;
        lock (_slotLock)
        {
            if (_activeSlots < _maxConcurrent) { _activeSlots++; return; }
            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _slotWaiters.Enqueue(tcs);
        }
        using (ct.Register(() => tcs.TrySetCanceled(ct)))
            await tcs.Task;
    }

    private void ReleaseSlot()
    {
        lock (_slotLock)
        {
            // Hand the freed slot to the next waiter (keeping the active count), else free it.
            while (_slotWaiters.Count > 0)
            {
                var w = _slotWaiters.Dequeue();
                if (w.TrySetResult()) return;
            }
            _activeSlots--;
        }
    }

    /// <summary>Fired (on a background thread) whenever any download's state changes.</summary>
    public event Action? Changed;

    public CivitaiDownloadManager(IConfiguration config, CivitaiService civitai)
    {
        _config = config;
        _civitai = civitai;

        if (int.TryParse(config["MaxConcurrentDownloads"], out var mc))
            _maxConcurrent = Math.Clamp(mc, 1, 16);

        // ModelsRoot defaults to the parent of the LoRA folder (the A1111 "models" dir).
        var loraPath = config["BaseLoraPath"] ?? @"";
        _modelsRoot = config["ModelsRoot"]
            ?? (Directory.Exists(loraPath) ? Directory.GetParent(loraPath)?.FullName : "")
            ?? loraPath;
    }

    /// <summary>All downloads this session, newest first (active + history).</summary>
    public IReadOnlyList<CivitaiDownload> All
    {
        get { lock (_orderLock) return _ordered.AsEnumerable().Reverse().ToList(); }
    }

    public int ActiveCount =>
        _downloads.Values.Count(d => d.Status is DownloadStatus.Downloading or DownloadStatus.Queued);

    public CivitaiDownload? Get(int versionId) =>
        _downloads.TryGetValue(versionId, out var d) ? d : null;

    /// <summary>True if this version is downloading or already finished this session.</summary>
    public bool IsKnown(int versionId) => _downloads.ContainsKey(versionId);

    /// <summary>Remove finished (completed/failed/cancelled) entries from the list; keeps active ones.</summary>
    public void ClearFinished()
    {
        lock (_orderLock)
        {
            var finished = _ordered
                .Where(d => d.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled)
                .ToList();

            foreach (var d in finished)
            {
                _ordered.Remove(d);
                _downloads.TryRemove(d.VersionId, out _);
            }
        }
        Notify();
    }

    // ---- On-disk library index (robust "already downloaded?" detection) ----

    private readonly object _indexLock = new();
    private Dictionary<string, string>? _fileIndex; // file name (case-insensitive) -> full path

    private static readonly string[] ModelExts =
        { ".safetensors", ".ckpt", ".pt", ".pth", ".bin" };

    /// <summary>
    /// Scan <see cref="_modelsRoot"/> (recursively) and index every model file by name. The
    /// result is cached until explicitly invalidated (after a download) or forced (on search/
    /// reload) — so it never triggers a surprise full-disk rescan during a render.
    /// </summary>
    public void RefreshLibraryIndex(bool force = false)
    {
        lock (_indexLock)
        {
            if (!force && _fileIndex != null)
                return;

            var idx = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (Directory.Exists(_modelsRoot))
                {
                    foreach (var f in Directory.EnumerateFiles(_modelsRoot, "*.*", SearchOption.AllDirectories))
                    {
                        if (ModelExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                            idx[Path.GetFileName(f)] = f; // last one wins on dup names
                    }
                }
            }
            catch { /* index is best-effort */ }

            _fileIndex = idx;
        }
    }

    /// <summary>
    /// Return the on-disk path of this version's file if it exists anywhere under the models
    /// root (any sub-folder), else null. Matches by file name (and our sanitized variant).
    /// </summary>
    public string? FindOnDisk(CivitaiModelVersion version)
    {
        var file = version.PrimaryFile;
        if (file is null || string.IsNullOrWhiteSpace(file.Name)) return null;

        RefreshLibraryIndex();

        lock (_indexLock)
        {
            if (_fileIndex is null) return null;

            // Trust the cached index rather than stat-ing the disk here: FindOnDisk runs per-version
            // per-card on every Civitai-grid render. The index is force-rebuilt at the start of each
            // search (CivitaiBrowser) and invalidated after every download, so it stays fresh.
            foreach (var key in new[] { file.Name, SanitizeFileName(file.Name) })
            {
                if (_fileIndex.TryGetValue(key, out var p))
                    return p;
            }
        }
        return null;
    }

    public bool IsDownloaded(CivitaiModelVersion version) => FindOnDisk(version) is not null;

    /// <summary>How many of the model's downloadable versions are present on disk, and the total.</summary>
    public (int Downloaded, int Total) DownloadCounts(CivitaiModel model)
    {
        var downloadable = model.ModelVersions.Where(v => v.PrimaryFile is not null).ToList();
        var have = downloadable.Count(v => FindOnDisk(v) is not null);
        return (have, downloadable.Count);
    }

    /// <summary>Per-version download state (for tooltips): name + whether it's on disk.</summary>
    public List<(string Name, bool Downloaded)> VersionStatuses(CivitaiModel model) =>
        model.ModelVersions
            .Where(v => v.PrimaryFile is not null)
            .Select(v => (v.Name, FindOnDisk(v) is not null))
            .ToList();

    /// <summary>True if any version of the model is currently downloading.</summary>
    public bool IsAnyDownloading(CivitaiModel model) =>
        model.ModelVersions.Any(v => Get(v.Id)?.Status == DownloadStatus.Downloading);

    /// <summary>Path of the first version of this model found on disk, or null.</summary>
    public string? FindAnyOnDisk(CivitaiModel model)
    {
        foreach (var v in model.ModelVersions)
        {
            var p = FindOnDisk(v);
            if (p is not null) return p;
        }
        return null;
    }

    /// <summary>The base type folder (Lora, Stable-diffusion, ...) for a model type.</summary>
    private string TypeFolder(string modelType)
    {
        var overridePath = _config[$"CivitaiFolders:{modelType}"];
        if (!string.IsNullOrWhiteSpace(overridePath))
            return overridePath;

        var sub = modelType.ToLowerInvariant() switch
        {
            "checkpoint" => "Stable-diffusion",
            "lora" or "locon" or "lycoris" => "Lora",
            "textualinversion" => "embeddings",
            "hypernetwork" => "hypernetworks",
            "vae" => "VAE",
            "controlnet" => "ControlNet",
            "upscaler" => "ESRGAN",
            "motionmodule" => "motion_module",
            "poses" => "Poses",
            _ => "Other"
        };
        return Path.Combine(_modelsRoot, sub);
    }

    /// <summary>
    /// Resolve the destination folder for a download:
    /// <c>&lt;typeFolder&gt;/_&lt;baseModel&gt;/&lt;modelName&gt;</c>.
    /// All versions of one model land in the same folder, each with its own sidecars.
    /// </summary>
    public string ResolveFolder(string modelType, string? baseModel, string modelName)
    {
        var bm = string.IsNullOrWhiteSpace(baseModel) ? "Unknown" : baseModel;
        return Path.Combine(
            TypeFolder(modelType),
            "_" + FolderSanitize(bm),    // "SD 1.5" -> "SD_1_5"
            FolderSanitize(modelName));  // "Hipoly 3D Model LoRA" -> "Hipoly_3D_Model_LoRA"
    }

    /// <summary>Start a download. Returns immediately; progress is reported via the returned object + <see cref="Changed"/>.</summary>
    public CivitaiDownload Start(CivitaiModel model, CivitaiModelVersion version, string? apiKey = null)
    {
        var file = version.PrimaryFile
            ?? throw new InvalidOperationException("This version has no downloadable file.");

        var downloadUrl = file.DownloadUrl ?? version.DownloadUrl
            ?? throw new InvalidOperationException("This version has no download URL.");

        var folder = ResolveFolder(model.Type, version.BaseModel, model.Name);
        Directory.CreateDirectory(folder);

        var safeName = SanitizeFileName(file.Name);
        var targetPath = Path.Combine(folder, safeName);

        var dl = new CivitaiDownload
        {
            VersionId = version.Id,
            ModelId = model.Id,
            ModelName = model.Name,
            FileName = safeName,
            TargetPath = targetPath,
            TotalBytes = (long)(file.SizeKB * 1024),
            ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim(),
            Model = model,
            Version = version,
            DownloadUrl = downloadUrl,
        };

        // Replace any previous (failed/cancelled/paused) entry for this version.
        if (_downloads.TryGetValue(version.Id, out var prev))
        {
            prev.PauseRequested = false;
            try { prev.Cts.Cancel(); } catch { }
            lock (_orderLock) _ordered.Remove(prev);
        }

        _downloads[version.Id] = dl;
        lock (_orderLock) _ordered.Add(dl);

        _ = Task.Run(() => RunAsync(dl, resume: false));
        return dl;
    }

    /// <summary>Start downloads for every version of the model that has a downloadable file.</summary>
    public List<CivitaiDownload> StartAll(CivitaiModel model, string? apiKey = null)
    {
        var started = new List<CivitaiDownload>();
        foreach (var v in model.ModelVersions)
        {
            if (v.PrimaryFile is null) continue;
            try { started.Add(Start(model, v, apiKey)); } catch { /* skip versions that can't start */ }
        }
        return started;
    }

    /// <summary>
    /// Recover/refresh metadata for an existing model file: hash it, look it up on Civitai by hash,
    /// then (re)write the json/txt/tags/html + preview-image sidecars. Returns false if no match.
    /// </summary>
    public async Task<bool> RefreshMetadataAsync(string modelFilePath, string? apiKey, CancellationToken ct = default)
    {
        if (!File.Exists(modelFilePath)) return false;

        var hash = await ResolveSha256Async(modelFilePath, ct);

        var version = await _civitai.GetModelVersionByHashAsync(hash, apiKey, ct);
        if (version is null) return false;

        var model = await _civitai.GetModelByIdAsync(version.ModelId, apiKey, ct);
        if (model is null) return false;

        var matched = model.ModelVersions.FirstOrDefault(v => v.Id == version.Id) ?? version;
        await WriteSidecarsForFileAsync(modelFilePath, model, matched, ct);

        lock (_indexLock) _fileIndex = null;
        return true;
    }

    /// <summary>Result of an update check: the model, the on-disk version, and a newer one if any.</summary>
    public sealed record UpdateCheckResult(CivitaiModel? Model, CivitaiModelVersion? Current, CivitaiModelVersion? Newer);

    /// <summary>
    /// Check whether a newer version exists for the SAME base model as the file on disk.
    /// (Different base models are treated as separate lineages, so e.g. an SDXL version is not
    /// reported as an "update" to an SD 1.5 one.)
    /// </summary>
    public async Task<UpdateCheckResult> CheckForUpdateAsync(string modelFilePath, string? apiKey, CancellationToken ct = default)
    {
        if (!File.Exists(modelFilePath)) return new(null, null, null);

        var hash = await ResolveSha256Async(modelFilePath, ct);
        var found = await _civitai.GetModelVersionByHashAsync(hash, apiKey, ct);
        if (found is null) return new(null, null, null);

        var model = await _civitai.GetModelByIdAsync(found.ModelId, apiKey, ct);
        if (model is null) return new(null, found, null);

        var current = model.ModelVersions.FirstOrDefault(v => v.Id == found.Id) ?? found;

        static DateTimeOffset When(CivitaiModelVersion v) =>
            v.PublishedAt ?? v.CreatedAt ?? DateTimeOffset.MinValue;

        var best = model.ModelVersions
            .Where(v => v.PrimaryFile is not null
                        && string.Equals(v.BaseModel, current.BaseModel, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(When)
            .ThenByDescending(v => v.Id)
            .FirstOrDefault();

        var newer = (best is not null && best.Id != current.Id) ? best : null;
        return new(model, current, newer);
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct = default)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var hash = await sha.ComputeHashAsync(fs, ct);
        return Convert.ToHexString(hash); // uppercase, matches Civitai's SHA256
    }

    // Cache hashes per file so check-update + fetch-metadata don't re-hash a big file twice.
    private readonly ConcurrentDictionary<string, (long Size, long Mtime, string Hash)> _hashCache = new(StringComparer.OrdinalIgnoreCase);

    private async Task<string> GetSha256Async(string path, CancellationToken ct)
    {
        try
        {
            var fi = new FileInfo(path);
            long size = fi.Length, mtime = fi.LastWriteTimeUtc.Ticks;
            if (_hashCache.TryGetValue(path, out var c) && c.Size == size && c.Mtime == mtime)
                return c.Hash;

            var hash = await ComputeSha256Async(path, ct);
            _hashCache[path] = (size, mtime, hash);
            return hash;
        }
        catch
        {
            return await ComputeSha256Async(path, ct);
        }
    }

    /// <summary>
    /// Resolve a model file's SHA256, preferring the hash already stored in its <c>.civitai.json</c>
    /// sidecar (instant) over hashing the file (reads the whole multi-GB file — a CPU/disk burst
    /// that can make a local machine stutter). Falls back to computing it when no sidecar hash
    /// exists. Used by check-for-updates and refresh-metadata, where re-hashing was the source of UI lag.
    /// </summary>
    private async Task<string> ResolveSha256Async(string modelFilePath, CancellationToken ct)
        => TryReadSidecarSha256(modelFilePath) ?? await GetSha256Async(modelFilePath, ct);

    /// <summary>The SHA256 recorded for this file in its sidecar, or null if absent/unreadable.</summary>
    private static string? TryReadSidecarSha256(string modelFilePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(modelFilePath);
            if (dir is null) return null;
            var jsonPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(modelFilePath) + ".civitai.json");
            if (!File.Exists(jsonPath)) return null;

            using var s = File.OpenRead(jsonPath);
            using var doc = JsonDocument.Parse(s);
            if (!doc.RootElement.TryGetProperty("modelVersions", out var mvs) || mvs.ValueKind != JsonValueKind.Array)
                return null;

            var targetFile = Path.GetFileName(modelFilePath);
            string? firstAny = null;

            foreach (var mv in mvs.EnumerateArray())
            {
                if (!mv.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) continue;
                foreach (var f in files.EnumerateArray())
                {
                    if (!f.TryGetProperty("hashes", out var hashes) || hashes.ValueKind != JsonValueKind.Object) continue;
                    if (!hashes.TryGetProperty("SHA256", out var sha) || sha.ValueKind != JsonValueKind.String) continue;
                    var hash = sha.GetString();
                    if (string.IsNullOrWhiteSpace(hash)) continue;

                    // The sidecar can list several files; prefer the entry whose name matches the
                    // actual model file, but accept the first hash found as a fallback.
                    var name = f.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                    if (name is null || string.Equals(name, targetFile, StringComparison.OrdinalIgnoreCase))
                        return hash.ToUpperInvariant();
                    firstAny ??= hash.ToUpperInvariant();
                }
            }
            return firstAny;
        }
        catch { return null; }
    }

    /// <summary>Cancel a download and discard its partial file (works while downloading or paused).</summary>
    public void Cancel(int versionId)
    {
        if (!_downloads.TryGetValue(versionId, out var dl)) return;

        if (dl.Status == DownloadStatus.Paused)
        {
            // Not running — flip the state and clean up directly.
            dl.Status = DownloadStatus.Cancelled;
            TryDelete(dl.TargetPath + ".part");
            Notify();
        }
        else
        {
            dl.PauseRequested = false;
            try { dl.Cts.Cancel(); } catch { }
        }
    }

    /// <summary>Restart a cancelled/failed download from scratch.</summary>
    public void Restart(int versionId)
    {
        if (_downloads.TryGetValue(versionId, out var dl) && dl.Model is not null && dl.Version is not null)
            Start(dl.Model, dl.Version, dl.ApiKey);
    }

    /// <summary>Pause a download, keeping its partial file so it can resume.</summary>
    public void Pause(int versionId)
    {
        if (_downloads.TryGetValue(versionId, out var dl) && dl.Status == DownloadStatus.Downloading)
        {
            dl.PauseRequested = true;
            try { dl.Cts.Cancel(); } catch { }
        }
    }

    /// <summary>Resume a paused (or failed) download from where its partial file left off.</summary>
    public void Resume(int versionId)
    {
        if (_downloads.TryGetValue(versionId, out var dl) && dl.CanResume)
        {
            dl.Cts = new CancellationTokenSource();
            dl.PauseRequested = false;
            dl.Error = null;
            _ = Task.Run(() => RunAsync(dl, resume: true));
        }
    }

    private async Task RunAsync(CivitaiDownload dl, bool resume)
    {
        var tempPath = dl.TargetPath + ".part";

        // Wait for a free slot (max concurrency). The download stays "Queued" until one opens.
        try
        {
            dl.Status = DownloadStatus.Queued;
            Notify();
            await AcquireSlotAsync(dl.Cts.Token);
        }
        catch (OperationCanceledException)
        {
            if (dl.PauseRequested) dl.Status = DownloadStatus.Paused;
            else { dl.Status = DownloadStatus.Cancelled; TryDelete(tempPath); }
            Notify();
            return;
        }

        try
        {
            dl.Status = DownloadStatus.Downloading;
            dl.PauseRequested = false;
            Notify();

            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SimpleDiffusion/1.0");

            var url = dl.DownloadUrl;
            if (!string.IsNullOrWhiteSpace(dl.ApiKey))
                url += (url.Contains('?') ? "&" : "?") + "token=" + Uri.EscapeDataString(dl.ApiKey);

            long existing = (resume && File.Exists(tempPath)) ? new FileInfo(tempPath).Length : 0;

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (existing > 0)
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, dl.Cts.Token);

            // Did the server honour the resume request?
            var append = existing > 0 && resp.StatusCode == System.Net.HttpStatusCode.PartialContent;
            if (existing > 0 && !append)
                existing = 0; // server ignored Range (returned 200) — restart from scratch

            resp.EnsureSuccessStatusCode();

            if (append && resp.Content.Headers.ContentRange?.Length is long full && full > 0)
                dl.TotalBytes = full;
            else if (resp.Content.Headers.ContentLength is long len && len > 0)
                dl.TotalBytes = existing + len;

            dl.BytesReceived = existing;

            await using (var src = await resp.Content.ReadAsStreamAsync(dl.Cts.Token))
            await using (var dst = new FileStream(tempPath, append ? FileMode.Append : FileMode.Create,
                                                  FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                var buffer = new byte[1 << 20]; // 1 MB
                int read;
                var lastNotify = DateTime.UtcNow;
                while ((read = await src.ReadAsync(buffer, dl.Cts.Token)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), dl.Cts.Token);
                    dl.BytesReceived += read;
                    // Throttle UI notifications to ~3/sec so progress doesn't spam re-renders.
                    var now = DateTime.UtcNow;
                    if ((now - lastNotify).TotalMilliseconds >= 350) { lastNotify = now; Notify(); }
                }
            }

            if (File.Exists(dl.TargetPath)) File.Delete(dl.TargetPath);
            File.Move(tempPath, dl.TargetPath);

            await WriteSidecarsAsync(dl, dl.Model, dl.Version);
            lock (_indexLock) _fileIndex = null;

            dl.Status = DownloadStatus.Completed;
            dl.BytesReceived = dl.TotalBytes;
            Notify();
        }
        catch (OperationCanceledException)
        {
            // Pause keeps the .part file; a real cancel discards it.
            if (dl.PauseRequested)
            {
                dl.Status = DownloadStatus.Paused;
            }
            else
            {
                dl.Status = DownloadStatus.Cancelled;
                TryDelete(tempPath);
            }
            Notify();
        }
        catch (Exception ex)
        {
            // Keep the partial file so the user can resume/retry.
            dl.Status = DownloadStatus.Failed;
            dl.Error = ex.Message;
            Notify();
        }
        finally
        {
            ReleaseSlot(); // free the slot for the next queued download
        }
    }

    /// <summary>
    /// Writes per-version metadata + all preview media alongside the model file so the existing
    /// LoRA browser (and its /lora-meta + /lora-previews endpoints) picks them up. Image file
    /// names are prefixed with the (per-version unique) model base name so multiple versions in
    /// the same folder never overwrite each other's previews.
    /// </summary>
    private Task WriteSidecarsAsync(CivitaiDownload dl, CivitaiModel model, CivitaiModelVersion version)
        => WriteSidecarsForFileAsync(dl.TargetPath, model, version, dl.Cts.Token);

    /// <summary>
    /// (Re)write the json/txt/tags/html + preview-media sidecars for an existing model file —
    /// used both by downloads and by the "refresh metadata" action.
    /// </summary>
    public async Task WriteSidecarsForFileAsync(string targetPath, CivitaiModel model, CivitaiModelVersion version, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(targetPath)!;
        var baseName = Path.GetFileNameWithoutExtension(targetPath);

        // ---- 1) Download ALL preview media (images + videos) for this version ----
        var savedMedia = new List<(string FileName, CivitaiImage Image, bool IsVideo)>();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SimpleDiffusion/1.0");
            http.DefaultRequestHeaders.Referrer = new Uri("https://civitai.com/");

            // The "card" preview (exact <base>.<ext>) — first non-NSFW still image if possible.
            var images = version.Images;
            var previewIdx = images.FindIndex(i => !i.RenderAsVideo && !i.IsNsfw);
            if (previewIdx < 0) previewIdx = images.FindIndex(i => !i.RenderAsVideo);
            if (previewIdx < 0) previewIdx = 0;

            for (var i = 0; i < images.Count; i++)
            {
                var img = images[i];
                if (string.IsNullOrWhiteSpace(img.Url)) continue;

                var ext = GuessMediaExt(img.Url, img.RenderAsVideo);
                // Exact base name for the card preview; unique suffixed names for the rest.
                var fileName = (i == previewIdx) ? baseName + ext : $"{baseName}.preview.{i}{ext}";
                var path = Path.Combine(dir, fileName);

                try
                {
                    var bytes = await http.GetByteArrayAsync(img.Url, ct);
                    await File.WriteAllBytesAsync(path, bytes, ct);
                    savedMedia.Add((fileName, img, img.RenderAsVideo));
                }
                catch (OperationCanceledException) { throw; }
                catch { /* skip this one image */ }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* media is best-effort */ }

        // ---- 2) <base>.civitai.json — full model data with THIS version first ----
        // (LoraDetailsServer reads name/creator/modelVersions[0]; putting the downloaded
        //  version first makes the local browser show the right base model + trigger words.)
        try
        {
            var payload = new
            {
                id = model.Id,
                modelId = model.Id,
                modelName = model.Name,
                name = model.Name,
                type = model.Type,
                nsfw = model.Nsfw,
                nsfwLevel = model.NsfwLevel,
                description = model.Description,
                tags = model.Tags,
                creator = model.Creator,
                stats = model.Stats,
                downloadedVersionId = version.Id,
                modelVersions = model.ModelVersions
                    .OrderByDescending(v => v.Id == version.Id)
                    .ToList(),
            };
            var jsonPath = Path.Combine(dir, baseName + ".civitai.json");
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(jsonPath, json, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        // ---- 3) <base>.txt — trigger words ----
        try
        {
            if (version.TrainedWords.Count > 0)
                await File.WriteAllTextAsync(Path.Combine(dir, baseName + ".txt"),
                    string.Join(", ", version.TrainedWords), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        // ---- 4) tags.txt — model-level tags (one per model folder, shared across versions) ----
        try
        {
            if (model.Tags.Count > 0)
                await File.WriteAllTextAsync(Path.Combine(dir, "tags.txt"),
                    string.Join(", ", model.Tags), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        // ---- 5) <base>.html — self-contained details page (embeds the local images) ----
        try
        {
            var html = BuildModelHtml(model, version, savedMedia);
            await File.WriteAllTextAsync(Path.Combine(dir, baseName + ".html"), html, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { }
    }

    private static string BuildModelHtml(
        CivitaiModel model,
        CivitaiModelVersion version,
        List<(string FileName, CivitaiImage Image, bool IsVideo)> media)
    {
        static string Size(double kb)
        {
            string[] u = { "KB", "MB", "GB", "TB" };
            double v = kb; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return $"{v:0.#} {u[i]}";
        }

        var modelLink = $"https://civitai.com/models/{model.Id}?modelVersionId={version.Id}";
        var userLink = model.Creator?.Username is { } un
            ? $"https://civitai.com/user/{Uri.EscapeDataString(un)}"
            : null;
        var file = version.PrimaryFile;
        var sb = new System.Text.StringBuilder();

        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append($"<title>{H(model.Name)} — {H(version.Name)}</title>");
        sb.Append(@"<style>
  :root{color-scheme:dark}
  body{margin:0;padding:18px;background:#0f1117;color:#e6e6e6;font:14px/1.6 system-ui,Segoe UI,Roboto,Arial}
  a{color:#9bbcff} h1{margin:.2em 0} h2{margin:1em 0 .3em} .muted{opacity:.7}
  table{border-collapse:collapse;margin:0 1em 1em 0}
  caption{text-align:left;font-weight:700;opacity:.8;padding:.2em 0}
  th,td{border:1px solid #2c3346;padding:.35em .6em;text-align:left;vertical-align:top}
  th{background:#161b29;white-space:nowrap}
  .info{display:flex;flex-wrap:wrap;align-items:flex-start}
  .chips span{display:inline-block;background:#1d2230;border:1px solid #2c3346;border-radius:999px;padding:2px 10px;margin:2px;font-size:12px}
  .basebadge{background:#2e7d32;color:#fff;border-radius:6px;padding:1px 8px}
  .imgs{display:grid;grid-template-columns:repeat(auto-fill,minmax(240px,1fr));gap:14px;margin-top:10px}
  .imgs figure{margin:0;background:#151926;border:1px solid #2c3346;border-radius:10px;overflow:hidden}
  .imgs img,.imgs video{width:100%;display:block;cursor:zoom-in}
  .imgs figcaption{padding:8px;font-size:12px;white-space:pre-wrap;word-break:break-word;max-height:160px;overflow:auto}
  .rows{margin-top:10px}
  .imgrow{display:flex;gap:1em;margin-top:1.2em;align-items:flex-start}
  .imgrow .media{flex:0 0 35%;max-width:35%}
  .imgrow img,.imgrow video{width:100%;max-height:32em;object-fit:contain;display:block;cursor:zoom-in;border-radius:8px;background:#151926}
  .imgrow .meta{flex:1 1 65%;max-height:32em;overflow-y:auto;overflow-wrap:anywhere;font-size:12px}
  .imgrow .meta p{margin:.3em 0}
  .imgrow .meta var{font-weight:700;font-style:normal}
  .imgrow .meta .kv span{display:inline-block;padding-right:.6em}
  .desc{margin-top:8px;overflow-wrap:anywhere} .desc img{max-width:100%;height:auto}
</style></head><body>");

        sb.Append($"<h1>{H(model.Name)}</h1>");
        sb.Append($"<p><a href=\"{H(modelLink)}\" target=\"_blank\" rel=\"noopener\">View on Civitai ↗</a></p>");

        // ---- Attribute tables (model + version) ----
        sb.Append("<div class=\"info\">");

        sb.Append("<table><caption>Model attributes</caption>");
        sb.Append($"<tr><th>ID</th><td><a href=\"{H(modelLink)}\" target=\"_blank\" rel=\"noopener\">{model.Id}</a></td></tr>");
        if (model.Creator?.Username is { } user)
            sb.Append($"<tr><th>Uploaded by</th><td><a href=\"{H(userLink!)}\" target=\"_blank\" rel=\"noopener\">{H(user)}</a></td></tr>");
        sb.Append($"<tr><th>Type</th><td>{H(model.Type)}</td></tr>");
        sb.Append($"<tr><th>NSFW</th><td>{(model.Nsfw ? "Yes" : "No")}</td></tr>");
        if (model.Stats is { } st)
        {
            sb.Append($"<tr><th>Downloads</th><td>{st.DownloadCount:N0}</td></tr>");
            sb.Append($"<tr><th>Likes</th><td>{st.ThumbsUpCount:N0}</td></tr>");
        }
        sb.Append("</table>");

        sb.Append("<table><caption>Version attributes</caption>");
        sb.Append($"<tr><th>ID</th><td><a href=\"{H(modelLink)}\" target=\"_blank\" rel=\"noopener\">{version.Id}</a></td></tr>");
        sb.Append($"<tr><th>Name</th><td>{H(version.Name)}</td></tr>");
        if (version.BaseModel is { } bm)
            sb.Append($"<tr><th>Base model</th><td><span class=\"basebadge\">{H(bm)}</span></td></tr>");
        if (file is not null)
        {
            sb.Append($"<tr><th>File</th><td>{H(file.Name)}</td></tr>");
            sb.Append($"<tr><th>Size</th><td>{Size(file.SizeKB)}</td></tr>");
            if (file.Sha256 is { } sha)
                sb.Append($"<tr><th>SHA256</th><td style=\"font-family:monospace;font-size:11px\">{H(sha)}</td></tr>");
        }
        var published = version.PublishedAt ?? version.CreatedAt;
        if (published is { } pub)
            sb.Append($"<tr><th>Published</th><td>{pub:yyyy-MM-dd}</td></tr>");
        if (version.TrainedWords.Count > 0)
            sb.Append($"<tr><th>Trigger words</th><td>{H(string.Join(", ", version.TrainedWords))}</td></tr>");
        sb.Append("</table>");

        sb.Append("</div>"); // .info

        if (model.Tags.Count > 0)
        {
            sb.Append("<h2>Tags</h2><div class=\"chips\">");
            foreach (var t in model.Tags) sb.Append($"<span>{H(t)}</span>");
            sb.Append("</div>");
        }

        if (!string.IsNullOrWhiteSpace(model.Description))
            sb.Append($"<h2>Model description</h2><div class=\"desc\">{model.Description}</div>");

        if (!string.IsNullOrWhiteSpace(version.Description))
            sb.Append($"<h2>Version description</h2><div class=\"desc\">{version.Description}</div>");

        if (media.Count > 0)
        {
            sb.Append("<h2>Images</h2><p class=\"muted\">Click an image to open it in the gallery viewer.</p>");

            // If any image carries generation metadata, lay them out one-per-row with the details
            // beside each. Otherwise fall back to a compact grid (images next to each other).
            var anyMeta = media.Any(m => HasMeta(m.Image));

            if (anyMeta)
            {
                sb.Append("<div class=\"rows sampleimgs\">");
                for (var i = 0; i < media.Count; i++)
                {
                    var m = media[i];
                    sb.Append("<div class=\"imgrow\"><div class=\"media\">");
                    sb.Append(MediaTag(m.FileName, m.IsVideo, i));
                    sb.Append("</div><div class=\"meta\">");
                    sb.Append(RenderImageMeta(m.Image));
                    sb.Append("</div></div>");
                }
                sb.Append("</div>");
            }
            else
            {
                sb.Append("<div class=\"imgs sampleimgs\">");
                for (var i = 0; i < media.Count; i++)
                {
                    var m = media[i];
                    sb.Append("<figure>");
                    sb.Append(MediaTag(m.FileName, m.IsVideo, i));
                    sb.Append("</figure>");
                }
                sb.Append("</div>");
            }
        }

        // Bridge: clicking an image asks the parent app to open the gallery viewer; when the
        // page is opened standalone (file://) it just opens the image in a new tab.
        sb.Append(@"<script>(function(){
  function open(idx, src){
    try{
      if(window.parent && window.parent !== window){
        window.parent.postMessage({type:'civitai-open-image', index:idx, src:src}, '*');
      } else { window.open(src, '_blank'); }
    }catch(e){ try{ window.open(src, '_blank'); }catch(_){} }
  }
  document.addEventListener('click', function(e){
    var el = e.target && e.target.closest ? e.target.closest('.civ-img') : null;
    if(!el) return;
    e.preventDefault();
    open(parseInt(el.dataset.idx,10)||0, el.dataset.file||'');
  }, true);
})();</script>");

        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static string H(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");

    private static string MediaTag(string fileName, bool isVideo, int idx) =>
        isVideo
            ? $"<video class=\"civ-img civ-novol\" src=\"{H(fileName)}\" data-idx=\"{idx}\" data-file=\"{H(fileName)}\" muted loop playsinline></video>"
            : $"<img class=\"civ-img\" src=\"{H(fileName)}\" data-idx=\"{idx}\" data-file=\"{H(fileName)}\" loading=\"lazy\">";

    private static bool HasMeta(CivitaiImage img)
    {
        if (!string.IsNullOrWhiteSpace(img.Prompt)) return true;
        return img.Meta is { ValueKind: JsonValueKind.Object } m && m.EnumerateObject().Any();
    }

    /// <summary>Render an image's generation metadata (prompt + scalar params) as HTML.</summary>
    private static string RenderImageMeta(CivitaiImage img)
    {
        if (img.Meta is not { ValueKind: JsonValueKind.Object } meta)
            return string.IsNullOrWhiteSpace(img.Prompt)
                ? "<p class=\"muted\">No metadata.</p>"
                : $"<p><var>prompt</var>: {H(img.Prompt)}</p>";

        var sb = new System.Text.StringBuilder();

        if (meta.TryGetProperty("prompt", out var p) && p.ValueKind == JsonValueKind.String)
            sb.Append($"<p><var>prompt</var>: {H(p.GetString())}</p>");
        if (meta.TryGetProperty("negativePrompt", out var np) && np.ValueKind == JsonValueKind.String)
            sb.Append($"<p><var>negativePrompt</var>: {H(np.GetString())}</p>");

        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "prompt", "negativePrompt" };
        var kv = new System.Text.StringBuilder();
        foreach (var prop in meta.EnumerateObject())
        {
            if (skip.Contains(prop.Name)) continue;
            string? val = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null // skip nested objects/arrays (hashes/resources)
            };
            if (val is null) continue;
            kv.Append($"<span><var>{H(prop.Name)}</var>: {H(val)}</span>");
        }
        if (kv.Length > 0) sb.Append($"<p class=\"kv\">{kv}</p>");

        if (sb.Length == 0) sb.Append("<p class=\"muted\">No metadata.</p>");
        return sb.ToString();
    }

    private static string GuessMediaExt(string url, bool isVideo)
    {
        var ext = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
        if (isVideo)
            return ext is ".mp4" or ".webm" or ".mov" or ".m4v" ? ext : ".mp4";
        return ImgExts.Contains(ext) ? ext : ".jpeg";
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    /// <summary>
    /// Folder-safe name: replace only spaces and dots with underscores (e.g. "SD 1.5" -> "SD_1_5"),
    /// keeping every other symbol that is legal in a Windows folder name. Invalid path characters
    /// are also replaced. Consecutive underscores are collapsed and trimmed.
    /// </summary>
    private static string FolderSanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        var lastUnderscore = false;
        foreach (var ch in name)
        {
            var replace = ch == ' ' || ch == '.' || invalid.Contains(ch);
            if (replace)
            {
                if (!lastUnderscore) { sb.Append('_'); lastUnderscore = true; }
            }
            else
            {
                sb.Append(ch);
                lastUnderscore = false;
            }
        }
        var result = sb.ToString().Trim('_', ' ');
        return string.IsNullOrEmpty(result) ? "_" : result;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private void Notify() => Changed?.Invoke();
}
