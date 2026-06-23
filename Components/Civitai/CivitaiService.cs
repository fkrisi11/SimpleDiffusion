using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;

namespace SimpleDiffusion.Components.Civitai;

/// <summary>
/// Typed client for the Civitai public REST API. The API key is per-device, so it is passed
/// in per call (attached as a Bearer header) rather than baked into the client.
/// </summary>
public sealed class CivitaiService
{
    private const string BaseUrl = "https://civitai.com/api/v1/";

    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public CivitaiService()
    {
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SimpleDiffusion/1.0");
    }

    private async Task<T?> GetAsync<T>(string url, string? apiKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return default;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<T>(JsonOpts, ct);
    }

    /// <summary>Search models. The returned metadata carries the cursor for the next page.</summary>
    public async Task<CivitaiSearchResult> SearchModelsAsync(CivitaiQuery query, string? apiKey = null, CancellationToken ct = default)
    {
        var qs = HttpUtility.ParseQueryString(string.Empty);

        qs["limit"] = Math.Clamp(query.Limit, 1, 100).ToString();
        qs["sort"] = query.Sort.ToApiValue();
        qs["period"] = query.Period.ToApiValue();

        // Prefer cursor when one is supplied (the API hands it back for some queries);
        // otherwise use page-based paging so we get a total page count + can jump pages.
        if (!string.IsNullOrWhiteSpace(query.Cursor))
            qs["cursor"] = query.Cursor;
        else if (query.Page > 0)
            qs["page"] = query.Page.ToString();

        if (!string.IsNullOrWhiteSpace(query.Query)) qs["query"] = query.Query.Trim();
        if (!string.IsNullOrWhiteSpace(query.Tag)) qs["tag"] = query.Tag.Trim();
        if (!string.IsNullOrWhiteSpace(query.Username)) qs["username"] = query.Username.Trim();
        if (query.Nsfw.HasValue) qs["nsfw"] = query.Nsfw.Value ? "true" : "false";
        // Undocumented numeric param the API uses to gate NSFW tiers; numeric values are accepted.
        if (query.BrowsingLevel.HasValue) qs["browsingLevel"] = query.BrowsingLevel.Value.ToString();

        // Civitai accepts repeated array params (types=A&types=B). ParseQueryString
        // can't hold duplicate keys cleanly, so we append these manually below.
        var url = "models?" + qs;
        foreach (var t in query.Types.Where(t => !string.IsNullOrWhiteSpace(t)))
            url += "&types=" + Uri.EscapeDataString(t);
        foreach (var b in query.BaseModels.Where(b => !string.IsNullOrWhiteSpace(b)))
            url += "&baseModels=" + Uri.EscapeDataString(b);

        return await GetAsync<CivitaiSearchResult>(url, apiKey, ct) ?? new CivitaiSearchResult();
    }

    /// <summary>Fetch a single model by its numeric id (used by the "model id" filter).</summary>
    public Task<CivitaiModel?> GetModelByIdAsync(int id, string? apiKey = null, CancellationToken ct = default)
        => GetAsync<CivitaiModel>($"models/{id}", apiKey, ct);

    /// <summary>Fetch a single model version by id.</summary>
    public Task<CivitaiModelVersion?> GetModelVersionAsync(int versionId, string? apiKey = null, CancellationToken ct = default)
        => GetAsync<CivitaiModelVersion>($"model-versions/{versionId}", apiKey, ct);

    /// <summary>Look up a model version by a file hash (SHA256/AutoV2/etc.) — used to recover metadata.</summary>
    public Task<CivitaiModelVersion?> GetModelVersionByHashAsync(string hash, string? apiKey = null, CancellationToken ct = default)
        => GetAsync<CivitaiModelVersion>($"model-versions/by-hash/{Uri.EscapeDataString(hash)}", apiKey, ct);
}
