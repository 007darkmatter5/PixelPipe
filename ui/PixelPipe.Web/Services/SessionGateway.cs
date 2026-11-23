using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace PixelPipe.Web.Services;

public sealed class SessionGateway
{
    private readonly IHttpClientFactory _http;
    private readonly TurnClient _turn;
    private readonly LibraryService _library;
    private readonly AccountStore _accounts;

    public SessionGateway(IHttpClientFactory http, TurnClient turn, LibraryService library, AccountStore accounts)
    {
        _http = http;
        _turn = turn;
        _library = library;
        _accounts = accounts;
    }

    public async Task<IResult> StartAsync(AccountUser user, string rom, string slot, CancellationToken cancellationToken)
    {
        var game = _library.Find(rom);
        if (game is null)
        {
            return Results.NotFound("That ROM is not in the library.");
        }

        IReadOnlyList<IceServer> ice;
        string? turnError = null;
        try
        {
            ice = await _turn.MintAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            ice = Array.Empty<IceServer>();
            turnError = ex.Message;
        }

        var chosen = string.IsNullOrWhiteSpace(slot) ? "1" : slot.Trim();
        var client = _http.CreateClient("engine");
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/session");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            EngineTokens.Issue(user.Id, user.Role, TimeSpan.FromMinutes(10)));
        request.Content = JsonContent.Create(new
        {
            rom,
            slot = chosen,
            ice_servers = ice.Select(server => new
            {
                urls = server.Urls,
                username = server.Username,
                credential = server.Credential,
            }),
        }, options: new System.Text.Json.JsonSerializerOptions());
        using var response = await client.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return Results.Text(text, statusCode: (int)response.StatusCode);
        }

        _accounts.TouchProgress(user.Id, game.ContentHash, game.SystemId, game.Title, game.RelativePath, chosen);
        var node = JsonNode.Parse(text)?.AsObject() ?? new JsonObject();
        node["iceServers"] = JsonValue.Create((object?)null);
        node["iceServers"] = System.Text.Json.JsonSerializer.SerializeToNode(ice.Select(server => new
        {
            urls = server.Urls,
            username = server.Username,
            credential = server.Credential,
        }));
        if (turnError is not null)
        {
            node["turnError"] = turnError;
        }

        return Results.Json(node);
    }
}
