using System.Collections.Concurrent;

namespace SimpleDiffusion.Components.Services;

/// <summary>
/// Supplies wildcard option lists for dynamic prompts. A wildcard <c>__name__</c> maps to
/// <c>wildcards/name.txt</c> (one option per line, blank lines and <c>#</c> comments ignored)
/// in the folder beside the app. Sub-folders are allowed (<c>__people/hair__</c>). Files are
/// cached and reloaded when their timestamp changes.
/// </summary>
public sealed class WildcardService
{
    private static string Dir => SimpleDiffusion.Infrastructure.AppPaths.WildcardsDir;
    private readonly ConcurrentDictionary<string, (DateTime Mtime, string[] Lines)> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Options for a wildcard name, or null if there is no such wildcard.</summary>
    public IReadOnlyList<string>? Get(string name)
    {
        var rel = Sanitize(name);
        if (rel is null) return null;
        var path = Path.Combine(Dir, rel + ".txt");
        if (!File.Exists(path)) return null;
        try
        {
            var mtime = File.GetLastWriteTimeUtc(path);
            if (_cache.TryGetValue(rel, out var c) && c.Mtime == mtime)
                return c.Lines.Length > 0 ? c.Lines : null;

            var lines = File.ReadAllLines(path)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("#"))
                .ToArray();
            _cache[rel] = (mtime, lines);
            return lines.Length > 0 ? lines : null;
        }
        catch { return null; }
    }

    /// <summary>All available wildcard names (relative, '/'-separated), for the help dialog.</summary>
    public List<string> ListNames()
    {
        if (!Directory.Exists(Dir)) return new();
        try
        {
            return Directory.EnumerateFiles(Dir, "*.txt", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(Dir, f))
                .Select(r => r[..^4].Replace('\\', '/'))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return new(); }
    }

    private static string? Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name.Trim().Replace('\\', '/');
        if (name.Contains("..")) return null;
        var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        foreach (var p in parts)
            if (p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        return Path.Combine(parts);
    }
}
