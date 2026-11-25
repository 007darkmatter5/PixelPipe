namespace PixelPipe.UpdateCore;

/// <summary>
/// Installers may only come from this repository's GitHub release downloads.
/// </summary>
public static class ReleaseSource
{
    public static bool TryGetChecksumUrl(string? sourceUrl, out string checksumUrl)
    {
        checksumUrl = "";
        if (string.IsNullOrWhiteSpace(sourceUrl) || !Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 6)
        {
            return false;
        }

        if (!parts[0].Equals("007darkmatter5", StringComparison.OrdinalIgnoreCase)
            || !parts[1].Equals("PixelPipe", StringComparison.OrdinalIgnoreCase)
            || !parts[2].Equals("releases", StringComparison.OrdinalIgnoreCase)
            || !parts[3].Equals("download", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var tag = Uri.UnescapeDataString(parts[4]);
        var name = Uri.UnescapeDataString(parts[5]);
        if (!tag.StartsWith('v') && !tag.StartsWith('V'))
        {
            return false;
        }

        if (tag.Contains("..", StringComparison.Ordinal) || name.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        if (!name.StartsWith("PixelPipe-Setup-", StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        checksumUrl = sourceUrl + ".sha256";
        return true;
    }
}
