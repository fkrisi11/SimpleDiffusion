using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimpleDiffusion.Components.Civitai;

// ============================================================================
// Raw API DTOs — these mirror the Civitai REST API shapes
// (https://github.com/civitai/civitai/wiki/REST-API-Reference).
// Only the fields the browser actually uses are mapped.
// ============================================================================

public sealed class CivitaiSearchResult
{
    [JsonPropertyName("items")] public List<CivitaiModel> Items { get; set; } = new();
    [JsonPropertyName("metadata")] public CivitaiMetadata Metadata { get; set; } = new();
}

public sealed class CivitaiMetadata
{
    [JsonPropertyName("totalItems")] public int? TotalItems { get; set; }
    [JsonPropertyName("currentPage")] public int? CurrentPage { get; set; }
    [JsonPropertyName("pageSize")] public int? PageSize { get; set; }
    [JsonPropertyName("totalPages")] public int? TotalPages { get; set; }
    [JsonPropertyName("nextCursor")] public string? NextCursor { get; set; }
    [JsonPropertyName("nextPage")] public string? NextPage { get; set; }
}

public sealed class CivitaiModel
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("nsfw")] public bool Nsfw { get; set; }
    [JsonPropertyName("nsfwLevel")] public int? NsfwLevel { get; set; }
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
    [JsonPropertyName("creator")] public CivitaiCreator? Creator { get; set; }
    [JsonPropertyName("stats")] public CivitaiStats? Stats { get; set; }
    [JsonPropertyName("modelVersions")] public List<CivitaiModelVersion> ModelVersions { get; set; } = new();

    /// <summary>The first/latest version, or null.</summary>
    [JsonIgnore]
    public CivitaiModelVersion? LatestVersion => ModelVersions.FirstOrDefault();

    /// <summary>First usable preview image across versions (for the card).</summary>
    [JsonIgnore]
    public CivitaiImage? CardImage =>
        ModelVersions.SelectMany(v => v.Images).FirstOrDefault();

    /// <summary>
    /// True if this model should be treated as NSFW: the boolean flag, an nsfwLevel of R or
    /// higher (4=R, 8=X, 16=XXX), or any version image flagged NSFW.
    /// </summary>
    [JsonIgnore]
    public bool IsNsfw =>
        Nsfw || (NsfwLevel is { } lvl && (lvl & CivitaiNsfw.NsfwMask) != 0);

    /// <summary>True if the model's nsfwLevel includes the XXX bit.</summary>
    [JsonIgnore]
    public bool HasXxx => NsfwLevel is { } lvl && (lvl & CivitaiNsfw.Xxx) != 0;
}

/// <summary>Civitai nsfwLevel bit flags and helpers.</summary>
public static class CivitaiNsfw
{
    public const int Pg = 1;
    public const int Pg13 = 2;
    public const int R = 4;
    public const int X = 8;
    public const int Xxx = 16;
    public const int Blocked = 32;

    /// <summary>R and above — what we consider "NSFW" for blurring/hiding.</summary>
    public const int NsfwMask = R | X | Xxx | Blocked;

    /// <summary>All levels through XXX — used to request explicit results from the API.</summary>
    public const int AllThroughXxx = Pg | Pg13 | R | X | Xxx;

    /// <summary>Comma-separated label like "R, X, XXX" for a combined nsfwLevel value.</summary>
    public static string Label(int? level)
    {
        if (level is not { } l || l <= 0) return "PG";
        var parts = new List<string>();
        if ((l & Pg) != 0) parts.Add("PG");
        if ((l & Pg13) != 0) parts.Add("PG-13");
        if ((l & R) != 0) parts.Add("R");
        if ((l & X) != 0) parts.Add("X");
        if ((l & Xxx) != 0) parts.Add("XXX");
        if ((l & Blocked) != 0) parts.Add("Blocked");
        return parts.Count > 0 ? string.Join(", ", parts) : "PG";
    }
}

public sealed class CivitaiCreator
{
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("image")] public string? Image { get; set; }
}

public sealed class CivitaiStats
{
    [JsonPropertyName("downloadCount")] public long DownloadCount { get; set; }
    [JsonPropertyName("favoriteCount")] public long FavoriteCount { get; set; }
    [JsonPropertyName("thumbsUpCount")] public long ThumbsUpCount { get; set; }
    [JsonPropertyName("thumbsDownCount")] public long ThumbsDownCount { get; set; }
    [JsonPropertyName("commentCount")] public long CommentCount { get; set; }
    [JsonPropertyName("rating")] public double Rating { get; set; }
    [JsonPropertyName("ratingCount")] public long RatingCount { get; set; }

    /// <summary>Approval rate from thumbs up/down (0..1), or null when there are no votes.</summary>
    [JsonIgnore]
    public double? ApprovalRate =>
        (ThumbsUpCount + ThumbsDownCount) > 0
            ? (double)ThumbsUpCount / (ThumbsUpCount + ThumbsDownCount)
            : null;
}

public sealed class CivitaiModelVersion
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("modelId")] public int ModelId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("baseModel")] public string? BaseModel { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("createdAt")] public DateTimeOffset? CreatedAt { get; set; }
    [JsonPropertyName("publishedAt")] public DateTimeOffset? PublishedAt { get; set; }
    [JsonPropertyName("downloadUrl")] public string? DownloadUrl { get; set; }
    [JsonPropertyName("trainedWords")] public List<string> TrainedWords { get; set; } = new();
    [JsonPropertyName("files")] public List<CivitaiFile> Files { get; set; } = new();
    [JsonPropertyName("images")] public List<CivitaiImage> Images { get; set; } = new();

    /// <summary>The primary downloadable file (the actual model weights).</summary>
    [JsonIgnore]
    public CivitaiFile? PrimaryFile =>
        Files.FirstOrDefault(f => f.Primary == true)
        ?? Files.FirstOrDefault(f => string.Equals(f.Type, "Model", StringComparison.OrdinalIgnoreCase))
        ?? Files.FirstOrDefault();
}

public sealed class CivitaiFile
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("sizeKB")] public double SizeKB { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("primary")] public bool? Primary { get; set; }
    [JsonPropertyName("downloadUrl")] public string? DownloadUrl { get; set; }
    [JsonPropertyName("hashes")] public Dictionary<string, string>? Hashes { get; set; }

    [JsonIgnore]
    public string? Sha256 =>
        Hashes != null && Hashes.TryGetValue("SHA256", out var h) ? h : null;
}

public sealed class CivitaiImage
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    // Nullable: when the API omits the property the value stays null. A default (Undefined)
    // JsonElement throws when re-serialized into our sidecar .civitai.json.
    [JsonPropertyName("nsfw")] public JsonElement? Nsfw { get; set; } // can be bool or string ("None","Soft",...)
    [JsonPropertyName("nsfwLevel")] public int? NsfwLevel { get; set; } // newer API field
    [JsonPropertyName("width")] public int? Width { get; set; }
    [JsonPropertyName("height")] public int? Height { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; } // "image" | "video"
    [JsonPropertyName("meta")] public JsonElement? Meta { get; set; } // generation params (prompt, etc.) — may be null

    [JsonIgnore]
    public bool IsVideo => string.Equals(Type, "video", StringComparison.OrdinalIgnoreCase);

    /// <summary>True if this image's nsfwLevel includes the XXX bit.</summary>
    [JsonIgnore]
    public bool HasXxx => NsfwLevel is { } lvl && (lvl & CivitaiNsfw.Xxx) != 0;

    /// <summary>
    /// Whether to render this with a &lt;video&gt; element. The API's <c>type</c> field is
    /// unreliable (video entries sometimes carry a still-image poster URL and vice-versa),
    /// so we decide by the actual file extension of the URL.
    /// </summary>
    [JsonIgnore]
    public bool RenderAsVideo => CivitaiImageUrl.IsVideoUrl(Url);

    /// <summary>The positive prompt / keywords used to generate this image, if Civitai exposes it.</summary>
    [JsonIgnore]
    public string? Prompt
    {
        get
        {
            if (Meta is { ValueKind: JsonValueKind.Object } meta &&
                meta.TryGetProperty("prompt", out var p) &&
                p.ValueKind == JsonValueKind.String)
                return p.GetString();
            return null;
        }
    }

    [JsonIgnore]
    public bool IsNsfw
    {
        get
        {
            if (Nsfw is { } n)
            {
                if (n.ValueKind == JsonValueKind.True) return true;
                if (n.ValueKind == JsonValueKind.String)
                {
                    var s = n.GetString();
                    return !string.IsNullOrEmpty(s) && !s.Equals("None", StringComparison.OrdinalIgnoreCase);
                }
            }
            // Newer API uses an nsfwLevel bitmask (PG=1, PG13=2, R=4, X=8, XXX=16, Blocked=32).
            // Treat R and above as NSFW via a bit test (so a combined value like R|X|XXX matches).
            return NsfwLevel is { } lvl && (lvl & CivitaiNsfw.NsfwMask) != 0;
        }
    }
}

// ============================================================================
// UI / query helpers
// ============================================================================

public enum CivitaiSort
{
    HighestRated,
    MostDownloaded,
    MostLiked,
    Newest,
    Oldest
}

public enum CivitaiPeriod
{
    AllTime,
    Year,
    Month,
    Week,
    Day
}

public static class CivitaiEnumExtensions
{
    // Civitai expects these exact strings on the query string.
    public static string ToApiValue(this CivitaiSort sort) => sort switch
    {
        CivitaiSort.HighestRated => "Highest Rated",
        CivitaiSort.MostDownloaded => "Most Downloaded",
        CivitaiSort.MostLiked => "Most Liked",
        CivitaiSort.Newest => "Newest",
        CivitaiSort.Oldest => "Oldest",
        _ => "Highest Rated"
    };

    public static string ToApiValue(this CivitaiPeriod period) => period switch
    {
        CivitaiPeriod.AllTime => "AllTime",
        CivitaiPeriod.Year => "Year",
        CivitaiPeriod.Month => "Month",
        CivitaiPeriod.Week => "Week",
        CivitaiPeriod.Day => "Day",
        _ => "AllTime"
    };
}

/// <summary>Parameters for a model search request.</summary>
public sealed class CivitaiQuery
{
    public string? Query { get; set; }            // free-text name search
    public string? Tag { get; set; }
    public string? Username { get; set; }
    public List<string> Types { get; set; } = new(); // "Checkpoint", "LORA", ...
    public List<string> BaseModels { get; set; } = new(); // "SD 1.5", "SDXL 1.0", "Pony", ...
    public CivitaiSort Sort { get; set; } = CivitaiSort.HighestRated;
    public CivitaiPeriod Period { get; set; } = CivitaiPeriod.AllTime;
    public bool? Nsfw { get; set; }
    public int? BrowsingLevel { get; set; }  // undocumented numeric bitmask the API uses to gate NSFW
    public int Limit { get; set; } = 50;
    public int Page { get; set; } = 1;       // page-based pagination (gives total page count)
    public string? Cursor { get; set; }      // cursor fallback when the API forces it
}

/// <summary>Helpers for Civitai's image CDN URLs.</summary>
public static class CivitaiImageUrl
{
    /// <summary>True if the URL points at a video file (by extension).</summary>
    public static bool IsVideoUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        try
        {
            var ext = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
            return ext is ".mp4" or ".webm" or ".mov" or ".m4v";
        }
        catch
        {
            var lower = url.ToLowerInvariant();
            return lower.Contains(".mp4") || lower.Contains(".webm");
        }
    }

    /// <summary>
    /// Civitai CDN URLs embed a "/width=N/" transform segment. Rewrite (or insert) it so we
    /// fetch an appropriately sized image instead of the full-res original.
    /// </summary>
    public static string WithWidth(string url, int width)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        try
        {
            var uri = new Uri(url);
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();

            var idx = segments.FindIndex(s => s.StartsWith("width=", StringComparison.OrdinalIgnoreCase)
                                           || s.StartsWith("original=", StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                segments[idx] = "width=" + width;
            else if (segments.Count >= 1)
                segments.Insert(segments.Count - 1, "width=" + width); // before the filename

            return $"{uri.Scheme}://{uri.Host}/{string.Join('/', segments)}";
        }
        catch
        {
            return url;
        }
    }
}

/// <summary>Known Civitai model types (for the type filter dropdown).</summary>
public static class CivitaiModelTypes
{
    public static readonly string[] All =
    {
        "Checkpoint", "LORA", "LoCon", "TextualInversion",
        "Hypernetwork", "AestheticGradient", "Controlnet",
        "Poses", "VAE", "Upscaler", "MotionModule", "Wildcards", "Workflows"
    };
}

/// <summary>Common base models for the filter dropdown (not exhaustive — Civitai adds new ones).</summary>
public static class CivitaiBaseModels
{
    public static readonly string[] Common =
    {
        "SD 1.5", "SD 2.1", "SDXL 1.0", "SDXL Turbo", "Pony",
        "Illustrious", "NoobAI", "Flux.1 D", "Flux.1 S", "SD 3.5"
    };
}
