using System.Net;
using Xunit;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using PixelPipe.Web.Services;

namespace PixelPipe.Web.Tests;

public class ProxyLoginTests : IClassFixture<ProxyLoginTests.ProxyFactory>
{
    private readonly ProxyFactory _factory;

    public ProxyLoginTests(ProxyFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Sign_in_works_behind_a_forwarded_https_host()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", "pixelpipe.lv426.cloud");
        var jar = new List<string>();

        var setup = await Send(client, HttpMethod.Get, "/setup", null, jar);
        var setupHtml = await setup.Content.ReadAsStringAsync();
        var created = await Send(client, HttpMethod.Post, "/setup", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(setupHtml),
            ["username"] = "Ada",
            ["password"] = "correct-horse",
            ["confirm"] = "correct-horse",
        }), jar);
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Assert.DoesNotContain("error=", created.Headers.Location?.OriginalString);

        var login = await Send(client, HttpMethod.Get, "/login", null, jar);
        var loginHtml = await login.Content.ReadAsStringAsync();
        var wrong = await Send(client, HttpMethod.Post, "/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(loginHtml),
            ["username"] = "ada",
            ["password"] = "not-the-password",
        }), jar);
        Assert.Equal(HttpStatusCode.Redirect, wrong.StatusCode);
        Assert.Contains("incorrect", Uri.UnescapeDataString(wrong.Headers.Location?.OriginalString ?? ""), StringComparison.OrdinalIgnoreCase);

        login = await Send(client, HttpMethod.Get, "/login", null, jar);
        loginHtml = await login.Content.ReadAsStringAsync();
        var ok = await Send(client, HttpMethod.Post, "/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(loginHtml),
            ["username"] = "ada",
            ["password"] = "correct-horse",
        }), jar);
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.True(
            ok.Headers.Location?.IsAbsoluteUri != true || ok.Headers.Location?.Scheme == "https",
            ok.Headers.Location?.ToString());
        var cookie = string.Join(";", ok.Headers.Select(header => header.Key == "Set-Cookie" ? string.Join(";", header.Value) : ""));
        Assert.Contains("PixelPipe.Auth", cookie, StringComparison.Ordinal);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Private_addresses_are_trusted_and_public_ones_are_not()
    {
        Assert.True(ProxyTrust.IsTrustedPeer(IPAddress.Parse("192.168.1.40"), trustAnyProxy: false));
        Assert.True(ProxyTrust.IsTrustedPeer(IPAddress.Loopback, trustAnyProxy: false));
        Assert.False(ProxyTrust.IsTrustedPeer(IPAddress.Parse("8.8.8.8"), trustAnyProxy: false));
        Assert.True(ProxyTrust.IsTrustedPeer(IPAddress.Parse("8.8.8.8"), trustAnyProxy: true));
    }

    [Fact]
    public void Usernames_match_without_regard_to_case()
    {
        var path = Path.Combine(Path.GetTempPath(), "pixelpipe-case-" + Guid.NewGuid().ToString("N") + ".db");
        var store = new AccountStore(path);
        store.Create("Ada", "correct-horse", "Admin");
        Assert.NotNull(store.FindByName("ada"));
        Assert.True(store.CheckPassword("ADA", "correct-horse").Ok);
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, HttpContent? content, List<string> jar)
    {
        var request = new HttpRequestMessage(method, path);
        if (jar.Count > 0)
        {
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", jar));
        }

        if (content is not null)
        {
            request.Content = content;
        }

        var response = await client.SendAsync(request);
        if (response.Headers.TryGetValues("Set-Cookie", out var set))
        {
            foreach (var value in set)
            {
                var pair = value.Split(';')[0];
                var name = pair.Split('=')[0];
                jar.RemoveAll(cookie => cookie.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
                jar.Add(pair);
            }
        }

        return response;
    }

    private static string Token(string html)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"");
        Assert.True(match.Success, html);
        return System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    public sealed class ProxyFactory : WebApplicationFactory<Program>
    {
        private readonly string _data = Path.Combine(Path.GetTempPath(), "pixelpipe-proxy-" + Guid.NewGuid().ToString("N"));

        public ProxyFactory()
        {
            Directory.CreateDirectory(_data);
            Environment.SetEnvironmentVariable("PIXELPIPE_DATA", _data);
        }
    }
}
