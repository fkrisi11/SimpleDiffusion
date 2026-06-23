using SimpleDiffusion.Components;
using System.Text.Json;

namespace SimpleDiffusion.Infrastructure;

public static class LoraDetailsServer
{
    public static void MapLoraDetails(this WebApplication app)
    {
        app.MapGet("/lora-meta", async (string loraPath) =>
        {
            var dir = Path.GetDirectoryName(loraPath);
            var baseName = Path.GetFileNameWithoutExtension(loraPath);

            if (string.IsNullOrEmpty(dir))
                return Results.NotFound();

            var txt = Path.Combine(dir, baseName + ".txt");
            var json = Path.Combine(dir, baseName + ".civitai.json");
            var html = Path.Combine(dir, baseName + ".html");

            var dto = new LoraMetaDto();

            if (System.IO.File.Exists(txt))
                dto.SidecarText = await System.IO.File.ReadAllTextAsync(txt);

            if (System.IO.File.Exists(html))
                dto.HtmlPage = await System.IO.File.ReadAllTextAsync(html);

            if (System.IO.File.Exists(json))
            {
                using var stream = System.IO.File.OpenRead(json);
                using var doc = await JsonDocument.ParseAsync(stream);

                var root = doc.RootElement;

                dto.CivitaiName = GetString(root, "name");
                dto.Creator = GetString(root, "creator", "username")
                           ?? GetString(root, "creator", "name");

                if (root.TryGetProperty("nsfwLevel", out var lvlEl) && lvlEl.ValueKind == JsonValueKind.Number
                    && lvlEl.TryGetInt32(out var lvl))
                    dto.NsfwLevel = lvl;

                var mv = GetFirst(root, "modelVersions") ?? default(JsonElement?);

                if (mv is { } mvEl)
                {
                    dto.BaseModel = GetString(mvEl, "baseModel");
                    dto.VersionName = GetString(mvEl, "name");

                    // trainedWords often lives here; entries may themselves be comma-joined, so
                    // split, trim, and de-duplicate (case-insensitively, keeping first-seen casing).
                    if (mvEl.TryGetProperty("trainedWords", out var tw) && tw.ValueKind == JsonValueKind.Array)
                        dto.TrainedWords = tw.EnumerateArray()
                            .Select(x => x.GetString())
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .SelectMany(x => x!.Split(','))
                            .Select(w => w.Trim())
                            .Where(w => w.Length > 0)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

                    // tags might be on root OR modelVersion
                    if (root.TryGetProperty("tags", out var tagsRoot) && tagsRoot.ValueKind == JsonValueKind.Array)
                        dto.Tags = tagsRoot.EnumerateArray()
                            .Select(x => x.GetString())
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .Cast<string>()
                            .Distinct()
                            .ToList();
                    else if (mvEl.TryGetProperty("tags", out var tagsMv) && tagsMv.ValueKind == JsonValueKind.Array)
                        dto.Tags = tagsMv.EnumerateArray()
                            .Select(x => x.GetString())
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .Cast<string>()
                            .Distinct()
                            .ToList();

                    // downloadUrl often inside modelVersion.files[0]
                    if (mvEl.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array && files.GetArrayLength() > 0)
                    {
                        var f0 = files[0];
                        dto.DownloadUrl = GetString(f0, "downloadUrl")
                                       ?? GetString(f0, "downloadUrl", "url"); // sometimes different exports
                    }
                }
            }

            return Results.Ok(dto);
        });

        string? GetString(JsonElement el, params string[] path)
        {
            foreach (var p in path)
            {
                if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(p, out el))
                    return null;
            }
            return el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
        }

        JsonElement? GetFirst(JsonElement el, params string[] path)
        {
            foreach (var p in path)
            {
                if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(p, out el))
                    return null;
            }
            if (el.ValueKind == JsonValueKind.Array && el.GetArrayLength() > 0)
                return el[0];
            return null;
        }
    }
}
