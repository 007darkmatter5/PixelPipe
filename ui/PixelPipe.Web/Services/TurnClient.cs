using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PixelPipe.Web.Services;

public sealed class IceServer
{
    public List<string> Urls { get; set; } = new();
    public string Username { get; set; } = "";
    public string Credential { get; set; } = "";
}

public sealed class TurnClient
{
    private readonly IHttpClientFactory _http;

    public TurnClient(IHttpClientFactory http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<IceServer>> MintAsync(CancellationToken cancellationToken)
    {
        var secrets = SecretBox.Load();
        var servers = new List<IceServer>();
        if (secrets.HasCloudflare)
        {
            var minted = await MintCloudflareAsync(secrets.CloudflareKeyId!, secrets.CloudflareToken!, cancellationToken);
            servers.AddRange(minted);
        }

        if (secrets.HasGeneric)
        {
            servers.Add(new IceServer
            {
                Urls = new List<string> { secrets.GenericUrl!.Trim() },
                Username = secrets.GenericUsername ?? "",
                Credential = secrets.GenericCredential ?? "",
            });
        }

        return servers;
    }

    public static async Task<IReadOnlyList<IceServer>> MintCloudflareAsync(
        HttpClient http,
        string keyId,
        string token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://rtc.live.cloudflare.com/v1/turn/keys/{Uri.EscapeDataString(keyId)}/credentials/generate-ice-servers");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent("{\"ttl\":3600}", Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Cloudflare TURN returned {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!doc.RootElement.TryGetProperty("iceServers", out var list))
        {
            return Array.Empty<IceServer>();
        }

        var servers = new List<IceServer>();
        foreach (var item in list.EnumerateArray())
        {
            var urls = new List<string>();
            if (item.TryGetProperty("urls", out var urlValue))
            {
                if (urlValue.ValueKind == JsonValueKind.Array)
                {
                    urls.AddRange(urlValue.EnumerateArray().Select(url => url.GetString() ?? "").Where(url => url.Length > 0));
                }
                else if (urlValue.GetString() is { Length: > 0 } one)
                {
                    urls.Add(one);
                }
            }

            servers.Add(new IceServer
            {
                Urls = urls,
                Username = item.TryGetProperty("username", out var user) ? user.GetString() ?? "" : "",
                Credential = item.TryGetProperty("credential", out var cred) ? cred.GetString() ?? "" : "",
            });
        }

        return servers;
    }

    private async Task<IReadOnlyList<IceServer>> MintCloudflareAsync(string keyId, string token, CancellationToken cancellationToken)
    {
        var http = _http.CreateClient("turn");
        return await MintCloudflareAsync(http, keyId, token, cancellationToken);
    }
}
