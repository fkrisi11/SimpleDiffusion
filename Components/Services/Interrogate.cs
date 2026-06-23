using System.Net.Http.Json;

namespace SimpleDiffusion.Components.Services;

/// <summary>Runs A1111's interrogate endpoint (CLIP caption or DeepDanbooru tags) for an image.
/// Shared so each gallery surface doesn't carry its own copy of the request/response DTOs.</summary>
public static class Interrogate
{
    public static async Task<string> RunAsync(HttpClient sdHttp, string base64, string model)
    {
        var resp = await sdHttp.PostAsJsonAsync("sdapi/v1/interrogate", new Request { image = base64, model = model });
        resp.EnsureSuccessStatusCode();
        var res = await resp.Content.ReadFromJsonAsync<Response>();
        return res?.caption ?? "(no caption returned)";
    }

    private sealed class Request
    {
        public string image { get; set; } = "";
        public string model { get; set; } = "clip";
    }

    private sealed class Response
    {
        public string? caption { get; set; }
    }
}
