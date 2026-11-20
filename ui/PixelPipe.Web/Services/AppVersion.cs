namespace PixelPipe.Web.Services;

/// <summary>SemVer with an optional prerelease label. A release build sorts after the same numbers with a label.</summary>
public readonly record struct AppVersion(int Major, int Minor, int Patch, string? PreRelease) : IComparable<AppVersion>
{
    public static bool TryParse(string? text, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        var plus = value.IndexOf('+');
        if (plus >= 0)
        {
            value = value[..plus];
        }

        string? pre = null;
        var dash = value.IndexOf('-');
        if (dash >= 0)
        {
            pre = value[(dash + 1)..];
            value = value[..dash];
        }

        var parts = value.Split('.');
        if (parts.Length is < 1 or > 3)
        {
            return false;
        }

        if (!int.TryParse(parts[0], out var major))
        {
            return false;
        }

        var minor = 0;
        var patch = 0;
        if (parts.Length > 1 && !int.TryParse(parts[1], out minor))
        {
            return false;
        }

        if (parts.Length > 2 && !int.TryParse(parts[2], out patch))
        {
            return false;
        }

        version = new AppVersion(major, minor, patch, string.IsNullOrEmpty(pre) ? null : pre);
        return true;
    }

    public int CompareTo(AppVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0)
        {
            return major;
        }

        var minor = Minor.CompareTo(other.Minor);
        if (minor != 0)
        {
            return minor;
        }

        var patch = Patch.CompareTo(other.Patch);
        if (patch != 0)
        {
            return patch;
        }

        if (PreRelease is null && other.PreRelease is null)
        {
            return 0;
        }

        if (PreRelease is null)
        {
            return 1;
        }

        if (other.PreRelease is null)
        {
            return -1;
        }

        return string.CompareOrdinal(PreRelease, other.PreRelease);
    }

    public override string ToString() => PreRelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";
}

public sealed record RemoteRelease(
    string Tag,
    AppVersion Version,
    bool Prerelease,
    string? Notes,
    string? HtmlUrl,
    long? ExeAssetId,
    long? ShaAssetId,
    string? ExeName = null);

public static class SetupAssets
{
    public static bool IsInstaller(string name)
    {
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return name.Equals("PixelPipe-Setup.exe", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("PixelPipe-Setup-", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsChecksumFor(string checksumName, string installerName) =>
        checksumName.Equals(installerName + ".sha256", StringComparison.OrdinalIgnoreCase)
        || checksumName.Equals("PixelPipe-Setup.exe.sha256", StringComparison.OrdinalIgnoreCase);

    public static (string Name, long Id)? PreferInstaller(IReadOnlyList<(string Name, long Id)> installers)
    {
        var versioned = installers
            .Where(item => item.Name.StartsWith("PixelPipe-Setup-", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (!string.IsNullOrEmpty(versioned.Name))
        {
            return versioned;
        }

        foreach (var item in installers)
        {
            if (item.Name.Equals("PixelPipe-Setup.exe", StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        return null;
    }
}

public static class ReleaseSelection
{
    public static RemoteRelease? NewerThan(IEnumerable<RemoteRelease> releases, AppVersion current, bool includePreviews)
    {
        RemoteRelease? best = null;
        foreach (var release in releases)
        {
            if (release.Prerelease && !includePreviews)
            {
                continue;
            }

            if (release.ExeAssetId is null || release.ShaAssetId is null)
            {
                continue;
            }

            if (release.Version.CompareTo(current) <= 0)
            {
                continue;
            }

            if (best is null || release.Version.CompareTo(best.Version) > 0)
            {
                best = release;
            }
        }

        return best;
    }
}

public static class ChecksumFile
{
    public static bool Matches(byte[] data, string shaFileText)
    {
        var expected = FirstToken(shaFileText);
        if (expected is null || expected.Length != 64)
        {
            return false;
        }

        var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
        return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
    }

    public static string? FirstToken(string shaFileText)
    {
        var token = shaFileText
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }
}
