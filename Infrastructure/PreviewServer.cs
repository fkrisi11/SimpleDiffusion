using System.Net.Mime;
using System.Text;
using System.Text.Json;

namespace SimpleDiffusion.Infrastructure;

public static class LoraPreviewEndpoints
{
    public static void MapLoraPreview(this WebApplication app)
    {
        app.MapGet("/lora-preview", (string loraPath, IConfiguration cfg) =>
        {
            var basePath = cfg["BaseLoraPath"] ?? @"C:\ai\stable-diffusion-webui-reForge\models\Lora";

            var fullLora = Path.GetFullPath(loraPath);
            var root = Path.GetFullPath(basePath);

            if (!fullLora.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return Results.Unauthorized();

            if (!System.IO.File.Exists(fullLora))
                return Results.NotFound();

            var preview = FindPreviewForLora(fullLora);

            if (preview == null || !System.IO.File.Exists(preview))
                return Results.Redirect("/images/card-no-preview.png");

            var contentType = GuessMediaContentType(preview);

            return Results.File(preview, contentType, enableRangeProcessing: true);
        });

        app.MapGet("/lora-previews", (string loraPath, IConfiguration cfg) =>
        {
            var basePath = cfg["BaseLoraPath"] ?? @"C:\ai\stable-diffusion-webui-reForge\models\Lora";
            var fullLora = Path.GetFullPath(loraPath);
            var root = Path.GetFullPath(basePath);

            if (!fullLora.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return Results.Unauthorized();

            if (!System.IO.File.Exists(fullLora))
                return Results.NotFound();

            var dir = Path.GetDirectoryName(fullLora);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                return Results.NotFound();

            var baseName = Path.GetFileNameWithoutExtension(fullLora);

            var files = FindAllPreviewCandidates(dir, baseName);

            var dto = new LoraPreviewListDto
            {
                LoraPath = fullLora,
                BaseName = baseName,
                Directory = dir,
                Items = files.Select(f =>
                {
                    var mime = GuessMediaContentType(f);
                    var kind = mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? "video" : "image";

                    return new LoraPreviewItemDto
                    {
                        FilePath = f,
                        FileName = Path.GetFileName(f),
                        Extension = Path.GetExtension(f).ToLowerInvariant(),
                        Mime = mime,
                        Kind = kind,
                        SizeBytes = new FileInfo(f).Length,
                        Url = $"/lora-preview-file?filePath={Uri.EscapeDataString(f)}"
                    };
                }).ToList()
            };

            return Results.Json(dto);
        });

        app.MapGet("/lora-preview-file", (string filePath, IConfiguration cfg) =>
        {
            var basePath = cfg["BaseLoraPath"] ?? @"C:\ai\stable-diffusion-webui-reForge\models\Lora";

            var full = Path.GetFullPath(filePath);
            var root = Path.GetFullPath(basePath);

            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return Results.Unauthorized();

            if (!System.IO.File.Exists(full))
                return Results.NotFound();

            var contentType = GuessMediaContentType(full);

            return Results.File(full, contentType, enableRangeProcessing: true);
        });
    }

    // ---------- helpers ----------

    private static readonly string[] ImgExts = [".webp", ".png", ".jpg", ".jpeg", ".gif", ".webm", ".mp4"];

    private static string? FindPreviewForLora(string loraFullPath)
    {
        var dir = Path.GetDirectoryName(loraFullPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return null;

        var loraBase = Path.GetFileNameWithoutExtension(loraFullPath);

        foreach (var ext in ImgExts)
        {
            var p = Path.Combine(dir, loraBase + ext);
            if (File.Exists(p)) return p;
        }

        var spaced = loraBase.Replace('_', ' ');
        foreach (var ext in ImgExts)
        {
            var p = Path.Combine(dir, spaced + ext);
            if (File.Exists(p)) return p;
        }

        var key = NormalizeName(loraBase);

        string? bestPath = null;
        double bestScore = 0;

        foreach (var f in Directory.EnumerateFiles(dir))
        {
            var ext = Path.GetExtension(f);
            if (!ImgExts.Contains(ext, StringComparer.OrdinalIgnoreCase))
                continue;

            var name = Path.GetFileNameWithoutExtension(f);
            var score = OverlapScore(key, NormalizeName(name));

            if (score > bestScore)
            {
                bestScore = score;
                bestPath = f;
            }
        }

        return bestScore >= 0.75 ? bestPath : null;
    }

    private static string NormalizeName(string s)
    {
        var chars = s.ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ')
            .ToArray();

        var cleaned = new string(chars);
        while (cleaned.Contains("  "))
            cleaned = cleaned.Replace("  ", " ");

        return cleaned.Trim();
    }

    private static double OverlapScore(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;

        var setA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var setB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        if (setA.Count == 0 || setB.Count == 0) return 0;

        var intersect = setA.Intersect(setB).Count();
        var union = setA.Union(setB).Count();
        return union == 0 ? 0 : (double)intersect / union;
    }

    private static string GuessVideoContentType(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            byte[] buffer = new byte[32];
            var read = fs.Read(buffer, 0, 32);

            if (read < 12) return "video/mp4"; // Fallback

            // 1. Check for WebM (EBML)
            if (buffer[0] == 0x1A && buffer[1] == 0x45 && buffer[2] == 0xDF && buffer[3] == 0xA3)
                return "video/webm";

            // 2. Check for MP4 'ftyp' (Bytes 4-7)
            if (Encoding.ASCII.GetString(buffer, 4, 4) == "ftyp")
            {
                // Bytes 8-11 contain the "Major Brand"
                string majorBrand = Encoding.ASCII.GetString(buffer, 8, 4);

                // 'hvc1' and 'hev1' are the standards for HEVC/H.265
                if (majorBrand == "hvc1" || majorBrand == "hev1")
                {
                    // We provide the codec string to help Firefox/Chrome select the decoder
                    // hvc1.1.6.L153.B0 is a common string for Main 10 profile
                    return "video/mp4; codecs=\"hvc1.1.6.L153.B0\"";
                }

                // 'avc1' is standard H.264
                if (majorBrand == "avc1")
                    return "video/mp4; codecs=\"avc1.42E01E\"";
            }

            return "video/mp4"; // Default fallback
        }
        catch
        {
            return "video/mp4";
        }
    }

    public static string GuessMediaContentType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();

        // Fast ext mapping first
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",

            // NOTE: your files can be "mp4 in a .webm coat"
            ".webm" or ".mp4" => GuessVideoContentType(path),

            _ => MediaTypeNames.Application.Octet
        };
    }

    private static List<string> FindAllPreviewCandidates(string dir, string loraBase)
    {
        // Prefer exact base name matches first, then spaced variant, then best fuzzy
        var exact = new List<string>();
        var spaced = new List<string>();
        var fuzzy = new List<(string path, double score)>();

        var spacedName = loraBase.Replace('_', ' ');
        var key = NormalizeName(loraBase);

        foreach (var ext in ImgExts)
        {
            var p1 = Path.Combine(dir, loraBase + ext);
            if (File.Exists(p1)) exact.Add(p1);

            var p2 = Path.Combine(dir, spacedName + ext);
            if (File.Exists(p2)) spaced.Add(p2);
        }

        // Also scan folder for anything else media-like and score it
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            var ext = Path.GetExtension(f);
            if (!ImgExts.Contains(ext, StringComparer.OrdinalIgnoreCase))
                continue;

            // don't duplicate exact/spaced already added
            if (exact.Contains(f, StringComparer.OrdinalIgnoreCase) ||
                spaced.Contains(f, StringComparer.OrdinalIgnoreCase))
                continue;

            var name = Path.GetFileNameWithoutExtension(f);
            var score = OverlapScore(key, NormalizeName(name));
            if (score > 0) fuzzy.Add((f, score));
        }

        // Ordering:
        // 1) exact matches
        // 2) spaced matches
        // 3) fuzzy by score desc
        // Within each group, prefer videos last or first? Up to you.
        // Here: prefer images first (looks nicer for thumbnails), videos after.
        static int MediaPriority(string path)
        {
            var e = Path.GetExtension(path).ToLowerInvariant();
            return (e == ".webm" || e == ".mp4") ? 1 : 0;
        }

        var result = new List<string>();
        result.AddRange(exact.OrderBy(MediaPriority));
        result.AddRange(spaced.OrderBy(MediaPriority));
        result.AddRange(fuzzy.OrderByDescending(x => x.score).Select(x => x.path).OrderBy(MediaPriority));

        // Remove duplicates just in case
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

}
