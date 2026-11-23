namespace PixelPipe.Web.Services;

public static class SaveLayout
{
    public static string ProgressDirectory(string dataDirectory, string userId, string system, string gameHash) =>
        Path.Combine(dataDirectory, "saves", Sanitize(userId), Sanitize(system), Sanitize(gameHash));

    public static string Sanitize(string value)
    {
        var cleaned = new string(value.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-').Take(80).ToArray());
        return cleaned.Length == 0 ? "unknown" : cleaned;
    }

    public static int MigrateLegacy(string dataDirectory, string adminId, IEnumerable<(string Crc32, string? RomCrc32, string SystemId, string ContentHash)> games)
    {
        var saves = Path.Combine(dataDirectory, "saves");
        if (!Directory.Exists(saves))
        {
            return 0;
        }

        var moved = 0;
        foreach (var file in Directory.EnumerateFiles(saves))
        {
            var name = Path.GetFileName(file);
            var stem = Path.GetFileNameWithoutExtension(name);
            var ext = Path.GetExtension(name);
            if (ext is not (".srm" or ".state"))
            {
                continue;
            }

            var match = games.FirstOrDefault(game =>
                string.Equals(game.Crc32, stem, StringComparison.OrdinalIgnoreCase)
                || string.Equals(game.RomCrc32, stem, StringComparison.OrdinalIgnoreCase));
            var system = string.IsNullOrEmpty(match.SystemId) ? "unknown" : match.SystemId;
            var hash = string.IsNullOrEmpty(match.ContentHash) ? stem : match.ContentHash;
            var destDir = ProgressDirectory(dataDirectory, adminId, system, hash);
            Directory.CreateDirectory(destDir);
            var destName = ext.Equals(".srm", StringComparison.OrdinalIgnoreCase) ? "sram.bin" : "slot-1.state";
            var dest = Path.Combine(destDir, destName);
            if (!File.Exists(dest))
            {
                File.Move(file, dest);
                moved++;
            }
        }

        return moved;
    }
}
