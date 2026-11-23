using System.Security.Cryptography;
using System.Text.Json;

namespace PixelPipe.Web.Services;

public sealed class TurnSecrets
{
    public string? CloudflareKeyId { get; set; }
    public string? CloudflareToken { get; set; }
    public string? GenericUrl { get; set; }
    public string? GenericUsername { get; set; }
    public string? GenericCredential { get; set; }

    public bool HasCloudflare =>
        !string.IsNullOrWhiteSpace(CloudflareKeyId) && !string.IsNullOrWhiteSpace(CloudflareToken);

    public bool HasGeneric =>
        !string.IsNullOrWhiteSpace(GenericUrl);
}

public static class SecretBox
{
    private static string PathName => Path.Combine(AppPaths.DataDirectory(), "turn-secrets.bin");

    public static TurnSecrets Load()
    {
        if (!File.Exists(PathName))
        {
            return new TurnSecrets();
        }

        try
        {
            var stored = File.ReadAllBytes(PathName);
            var plain = OperatingSystem.IsWindows()
                ? ProtectedData.Unprotect(stored, null, DataProtectionScope.CurrentUser)
                : stored;
            return JsonSerializer.Deserialize<TurnSecrets>(plain) ?? new TurnSecrets();
        }
        catch
        {
            return new TurnSecrets();
        }
    }

    public static void Save(TurnSecrets secrets)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory());
        var plain = JsonSerializer.SerializeToUtf8Bytes(secrets);
        var stored = OperatingSystem.IsWindows()
            ? ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser)
            : plain;
        File.WriteAllBytes(PathName, stored);
    }

    public static string ProtectForLog(string? value) =>
        string.IsNullOrEmpty(value) ? "" : "(stored)";
}
