using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;

namespace PixelPipe.Web.Services;

public static class RomScanner
{
    public sealed record Found(
        string RelativePath,
        string SystemId,
        string Title,
        string FullPath,
        string Crc32,
        string? RomCrc32,
        long Bytes);

    public static List<Found> Scan(string root, string relativePrefix = "")
    {
        var found = new List<Found>();
        if (!Directory.Exists(root))
        {
            return found;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var ext = Extension(file);
            if (ext == "md" && LooksLikeText(file))
            {
                continue;
            }

            if (!IsCandidate(ext))
            {
                continue;
            }

            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            string systemId;
            string titleStem;
            string crc;
            string? romCrc;
            long bytes;
            if (ext == "zip")
            {
                var picked = PickZipEntry(file, root);
                if (picked is null)
                {
                    continue;
                }

                systemId = picked.Value.SystemId;
                relative += "#" + picked.Value.EntryName.Replace('\\', '/');
                titleStem = Path.GetFileNameWithoutExtension(picked.Value.EntryName);
                (crc, romCrc, bytes) = picked.Value.Hash;
            }
            else
            {
                var detected = SystemFromExtension(ext) ?? SystemFromParents(root, file);
                if (detected is null)
                {
                    continue;
                }

                systemId = detected;
                titleStem = Path.GetFileNameWithoutExtension(file);
                (crc, romCrc, bytes) = HashFile(file, systemId == "nes");
            }

            if (!string.IsNullOrEmpty(relativePrefix))
            {
                relative = relativePrefix.Trim('/') + "/" + relative;
            }

            found.Add(new Found(relative, systemId, Prettify(titleStem), file, crc, romCrc, bytes));
        }

        return found;
    }

    public static string? SystemFromFolderName(string name)
    {
        var compact = new string(name.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
        return compact switch
        {
            "nes" or "famicom" => "nes",
            "snes" or "supernintendo" or "superfamicom" => "snes",
            "genesis" or "megadrive" or "segagenesis" => "genesis",
            _ => null,
        };
    }

    private static bool IsCandidate(string ext) =>
        ext is "zip" or "bin" || SystemFromExtension(ext) is not null;

    private static string? SystemFromExtension(string ext) => ext switch
    {
        "nes" or "unf" or "unif" => "nes",
        "sfc" or "smc" => "snes",
        "md" or "gen" or "smd" => "genesis",
        _ => null,
    };

    private static string? SystemFromParents(string root, string file)
    {
        var dir = Path.GetDirectoryName(file);
        while (!string.IsNullOrEmpty(dir))
        {
            var name = Path.GetFileName(dir);
            var system = SystemFromFolderName(name);
            if (system is not null)
            {
                return system;
            }

            if (string.Equals(Path.GetFullPath(dir), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    private readonly record struct ZipPick(string EntryName, string SystemId, (string Full, string? Body, long Bytes) Hash);

    private static ZipPick? PickZipEntry(string zipPath, string root)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        ZipPick? best = null;
        long bestLength = -1;
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) || entry.FullName.Contains("..", StringComparison.Ordinal))
            {
                continue;
            }

            var entryExt = Extension(entry.FullName);
            var system = SystemFromExtension(entryExt)
                ?? SystemFromEntryFolders(entry.FullName)
                ?? SystemFromParents(root, zipPath);
            if (system is null)
            {
                continue;
            }

            if (entry.Length > bestLength)
            {
                bestLength = entry.Length;
                using var stream = entry.Open();
                best = new ZipPick(entry.FullName.Replace('\\', '/'), system, HashStream(stream, system == "nes"));
            }
        }

        return best;
    }

    private static string? SystemFromEntryFolders(string entryName)
    {
        var parts = entryName.Replace('\\', '/').Split('/');
        for (var i = parts.Length - 2; i >= 0; i--)
        {
            var system = SystemFromFolderName(parts[i]);
            if (system is not null)
            {
                return system;
            }
        }

        return null;
    }

    private static (string Full, string? Body, long Bytes) HashFile(string path, bool nes)
    {
        using var stream = File.OpenRead(path);
        return HashStream(stream, nes);
    }

    private static (string Full, string? Body, long Bytes) HashStream(Stream stream, bool nes)
    {
        var full = new Crc32();
        var body = new Crc32();
        var header = new byte[16];
        var read = stream.Read(header, 0, header.Length);
        full.Append(header.AsSpan(0, read));
        var ines = nes && read == 16 && header[0] == (byte)'N' && header[1] == (byte)'E' && header[2] == (byte)'S' && header[3] == 0x1a;
        var buffer = new byte[64 * 1024];
        long total = read;
        int n;
        while ((n = stream.Read(buffer)) > 0)
        {
            full.Append(buffer.AsSpan(0, n));
            if (ines)
            {
                body.Append(buffer.AsSpan(0, n));
            }

            total += n;
        }

        return (Hex(full), ines ? Hex(body) : null, total);
    }

    private static string Hex(Crc32 crc)
    {
        var hash = crc.GetCurrentHash();
        var value = hash.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(hash) : 0;
        return value.ToString("x8");
    }

    private static string Extension(string path) =>
        Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    private static bool LooksLikeText(string path)
    {
        try
        {
            var buffer = new byte[256];
            using var stream = File.OpenRead(path);
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                return false;
            }

            for (var i = 0; i < read; i++)
            {
                var b = buffer[i];
                var printable = b is >= 32 and < 127 || b is (byte)'\n' or (byte)'\r' or (byte)'\t';
                if (!printable)
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Prettify(string stem) =>
        string.Join(' ', stem.Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
