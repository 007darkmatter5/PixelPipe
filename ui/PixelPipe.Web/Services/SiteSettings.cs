using System.Text.Json;

namespace PixelPipe.Web.Services;

public sealed class SiteSettings
{
    public bool BehindProxy { get; set; }
    public string PublicUrl { get; set; } = "";

    private static string PathName => Path.Combine(AppPaths.DataDirectory(), "site.json");

    public static SiteSettings Load()
    {
        if (!File.Exists(PathName))
        {
            return new SiteSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<SiteSettings>(File.ReadAllText(PathName)) ?? new SiteSettings();
        }
        catch
        {
            return new SiteSettings();
        }
    }

    public static void Save(SiteSettings settings)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory());
        if (!string.IsNullOrWhiteSpace(settings.PublicUrl)
            && !Uri.TryCreate(settings.PublicUrl.Trim(), UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("Public URL must be an absolute address such as https://pixelpipe.lv426.cloud.");
        }

        settings.PublicUrl = settings.PublicUrl.Trim().TrimEnd('/');
        File.WriteAllText(PathName, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>
/// Cloudflare Access can assert identity with the Cf-Access-Jwt-Assertion header.
/// Verification is intentionally not implemented yet. Cookie accounts remain the only sign-in.
/// </summary>
public static class CloudflareAccessPlan
{
    public const string HeaderName = "Cf-Access-Jwt-Assertion";
    public const bool Enabled = false;
}
