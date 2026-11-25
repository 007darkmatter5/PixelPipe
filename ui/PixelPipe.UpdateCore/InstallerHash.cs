using System.Security.Cryptography;

namespace PixelPipe.UpdateCore;

public static class InstallerHash
{
    public static string Compute(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string? FirstToken(string? shaFileText)
    {
        if (string.IsNullOrWhiteSpace(shaFileText))
        {
            return null;
        }

        var token = shaFileText
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }

    public static bool EqualsHash(string? actual, string? expected)
    {
        var left = FirstToken(actual);
        var right = FirstToken(expected);
        return left is not null
            && right is not null
            && left.Length == 64
            && right.Length == 64
            && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
