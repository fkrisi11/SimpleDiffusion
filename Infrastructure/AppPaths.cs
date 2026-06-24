namespace SimpleDiffusion.Infrastructure;

/// <summary>
/// Central source of truth for on-disk locations.
///
/// User-configurable paths (SD server, LoRA folder, models root) default to EMPTY — the app ships
/// with no machine-specific paths and the user fills them in via the UI. The one exception is the
/// tag dictionaries, which the app holds itself (<see cref="DefaultTagPath"/>, relative to the app).
///
/// Everything the user creates or changes — settings, the image gallery, caches, presets, tag
/// groups, quick tags, and favourite/frequency stats — lives under a single <c>data/</c> folder
/// next to the app. Backing up (or restoring) <c>data/</c> preserves the user's entire setup and
/// images across an app update. Regenerable caches live in <c>data/cache/</c> so they can be
/// excluded from a backup if desired.
/// </summary>
public static class AppPaths
{
    /// <summary>The application's install directory (where the exe and its bundled content live).</summary>
    public static string AppRoot => AppContext.BaseDirectory;

    /// <summary>Default tag-dictionary folder: the tags shipped alongside the app. This is the only
    /// path with a non-empty default, because it's relative to the app rather than the user's setup.</summary>
    public static string DefaultTagPath => Path.Combine(AppRoot, "wwwroot", "tags");

    // ---- The backup-able data folder ----

    /// <summary>The single folder holding everything the user can change. Back this up to migrate.</summary>
    public static string DataRoot => Path.Combine(AppRoot, "data");

    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string PresetsFile => Path.Combine(DataRoot, "presets.json");
    public static string TagGroupsFile => Path.Combine(DataRoot, "tag_groups.json");
    public static string TagStatsFile => Path.Combine(DataRoot, "tag_stats.json");
    public static string LoraStatsFile => Path.Combine(DataRoot, "lora_stats.json");
    public static string QuickTagsFile => Path.Combine(DataRoot, "quick_tags.txt");
    public static string WildcardsDir => Path.Combine(DataRoot, "wildcards");
    public static string GalleryDir => Path.Combine(DataRoot, "gallery");

    // Regenerable caches — under data/cache/ so they can be excluded from backups.
    public static string CacheRoot => Path.Combine(DataRoot, "cache");
    public static string GalleryCacheDir => Path.Combine(CacheRoot, "gallery");
    public static string ResultsCacheDir => Path.Combine(CacheRoot, "results");

    /// <summary>Create the data folder structure and migrate any legacy files left at the app root by
    /// older versions. Call once at startup, before the configuration (settings.json) is loaded.</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(CacheRoot);
        MigrateLegacy(); // move old root-level data in before we materialise the empty target dirs
        Directory.CreateDirectory(WildcardsDir);
        Directory.CreateDirectory(GalleryDir);
        Directory.CreateDirectory(GalleryCacheDir);
        Directory.CreateDirectory(ResultsCacheDir);
    }

    /// <summary>One-time, best-effort move of pre-<c>data/</c> files into the new layout. Items that
    /// were already migrated (target exists) are left alone. Runs against both the current working
    /// directory and the app root, since older builds used <c>Directory.GetCurrentDirectory()</c>.</summary>
    private static void MigrateLegacy()
    {
        var dataFull = Path.GetFullPath(DataRoot);
        var roots = new[] { Directory.GetCurrentDirectory(), AppRoot }
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            // Never migrate from inside the data folder itself.
            if (root.StartsWith(dataFull, StringComparison.OrdinalIgnoreCase)) continue;

            TryMoveDir(Path.Combine(root, "gallery"), GalleryDir);
            TryMoveDir(Path.Combine(root, "gallery-cache"), GalleryCacheDir);
            TryMoveDir(Path.Combine(root, "results-cache"), ResultsCacheDir);
            TryMoveDir(Path.Combine(root, "wildcards"), WildcardsDir);

            TryMoveFile(Path.Combine(root, "presets.json"), PresetsFile);
            TryMoveFile(Path.Combine(root, "tag_groups.json"), TagGroupsFile);
            TryMoveFile(Path.Combine(root, "tag_stats.json"), TagStatsFile);
            TryMoveFile(Path.Combine(root, "lora_stats.json"), LoraStatsFile);
            // settings.json is copied (not moved): the empty shipped template can stay at the root.
            TryCopyFile(Path.Combine(root, "settings.json"), SettingsFile);
        }
    }

    private static void TryMoveDir(string src, string dst)
    {
        try
        {
            if (!Directory.Exists(src) || Directory.Exists(dst)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            Directory.Move(src, dst);
        }
        catch { /* best-effort */ }
    }

    private static void TryMoveFile(string src, string dst)
    {
        try
        {
            if (!File.Exists(src) || File.Exists(dst)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Move(src, dst);
        }
        catch { /* best-effort */ }
    }

    private static void TryCopyFile(string src, string dst)
    {
        try
        {
            if (!File.Exists(src) || File.Exists(dst)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst);
        }
        catch { /* best-effort */ }
    }
}
