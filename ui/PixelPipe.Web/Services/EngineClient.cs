using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PixelPipe.Web.Services;

public sealed class EngineClient
{
    private readonly IHttpClientFactory _http;

    public EngineClient(IHttpClientFactory http)
    {
        _http = http;
    }

    public async Task<EngineHealth?> HealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _http.CreateClient("engine");
            return await client.GetFromJsonAsync<EngineHealth>("api/health", cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    public async Task PostAsync(string path, object body, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _http.CreateClient("engine");
            using var response = await client.PostAsJsonAsync(path, body, cancellationToken);
        }
        catch
        {
            // The library page still rescans from the saved config if the engine is down.
        }
    }

    public async Task<JsonElement?> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _http.CreateClient("engine");
            using var response = await client.GetAsync(path, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<JsonElement>(stream, cancellationToken: cancellationToken);
        }
        catch
        {
            return null;
        }
    }
}

public sealed class EngineHealth
{
    public bool Ok { get; set; }
    public string? Version { get; set; }
    public string? Encoder { get; set; }

    [JsonPropertyName("encoder_label")]
    public string? EncoderLabel { get; set; }

    [JsonPropertyName("encoder_detail")]
    public string? EncoderDetail { get; set; }
}
