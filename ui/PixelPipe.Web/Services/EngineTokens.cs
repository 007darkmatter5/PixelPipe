using System.Security.Cryptography;
using System.Text;

namespace PixelPipe.Web.Services;

public static class EngineTokens
{
    public static string Issue(string userId, string role, TimeSpan lifetime)
    {
        var exp = DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds();
        var payload = "{\"sub\":\"" + userId + "\",\"exp\":" + exp + ",\"role\":\"" + role + "\"}";
        var body = "v1." + Base64Url(Encoding.UTF8.GetBytes(payload));
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(AppPaths.EnsureAuthSecret()));
        var mac = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        return body + "." + Base64Url(mac);
    }

    public static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
