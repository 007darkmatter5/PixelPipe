using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text.Json;
using PixelPipe.Web.Models;

namespace PixelPipe.Web.Services;

public sealed class LibraryService
{
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly object _gate = new();
    private LibrarySnapshot? _cache;

    public LibraryService(IConfiguration config, IWebHostEnvironment env)
    {
        _config = config;
        _env = env;
    }

    public string RomsPath
    {
        get
        {
            var configured = _config["PixelPipe:RomsPath"] ?? "roms";
            if (Path.IsPathRooted(configured) && Directory.Exists(configured))
            {
                return configured;
            }

            var fromContent = Path.GetFullPath(Path.Combine(_env.ContentRootPath, configured));
            if (Directory.Exists(fromContent))
            {
                return fromContent;
            }

            var fromCwd = Path.GetFullPath(configured);
            return Directory.Exists(fromCwd) ? fromCwd : fromContent;
        }
    }

    public string EngineUrl => (_config["PixelPipe:EngineUrl"] ?? "http://127.0.0.1:9090").TrimEnd('/');

    public string? PublicEngineUrl
    {
        get
        {
            var value = _config["PixelPipe:PublicEngineUrl"];
            return string.IsNullOrWhiteSpace(value) ? null : value.TrimEnd('/');
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _cache = null;
        }
    }

    public LibrarySnapshot Get()
    {
        lock (_gate)
        {
            if (_cache is { } cached && DateTimeOffset.UtcNow - cached.ScannedAt < TimeSpan.FromSeconds(5))
            {
                return cached;
            }

            _cache = Scan();
            return _cache;
        }
    }

    public GameEntry? Find(string relativePath)
    {
        var key = relativePath.Replace('\\', '/').TrimStart('/');
        return Get().Games.FirstOrDefault(game =>
            string.Equals(game.RelativePath, key, StringComparison.OrdinalIgnoreCase));
    }

    public string PlaceholderSvg(GameEntry? game, string relativePath)
    {
        var title = game?.Title ?? Path.GetFileNameWithoutExtension(relativePath);
        var system = game?.System ?? "ROM";
        var color = game?.SystemId switch
        {
            "snes" => "#7c5cff",
            "genesis" => "#2ec4b6",
            _ => "#e94560",
        };
        string Esc(string value) => System.Security.SecurityElement.Escape(value) ?? "";
        return $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="300" height="420" viewBox="0 0 300 420">
              <rect width="300" height="420" fill="#14141c"/>
              <rect width="300" height="78" fill="{color}"/>
              <text x="150" y="48" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="22" font-weight="700">PIXELPIPE</text>
              <text x="150" y="250" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="22">{Esc(title)}</text>
              <text x="150" y="360" text-anchor="middle" fill="#ddd" font-family="sans-serif" font-size="28" font-weight="700">{Esc(system)}</text>
            </svg>
            """;
    }

    private LibrarySnapshot Scan()
    {
        var root = RomsPath;
        var meta = LoadMeta(Path.Combine(root, "library.json"));
        var games = new List<GameEntry>();
        if (Directory.Exists(root))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
                var systemId = ext switch
                {
                    "nes" or "unf" or "unif" => "nes",
                    "sfc" or "smc" => "snes",
                    "md" or "gen" or "smd" => "genesis",
                    _ => null,
                };
                if (systemId is null)
                {
                    continue;
                }

                // `.md` is both Markdown and Mega Drive. A text file is documentation.
                if (ext == "md" && LooksLikeText(file))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                var (crc, romCrc, bytes) = HashRom(file, systemId == "nes");
                var stem = Path.GetFileNameWithoutExtension(file);
                var title = Prettify(stem);
                int? year = null;
                string? developer = null;
                string? description = null;
                if (meta.TryGetValue(relative, out var entry))
                {
                    if (!string.IsNullOrWhiteSpace(entry.Title))
                    {
                        title = entry.Title!;
                    }
                    year = entry.Year;
                    developer = entry.Developer;
                    description = entry.Description;
                }

                games.Add(new GameEntry
                {
                    RelativePath = relative,
                    Title = title,
                    System = systemId switch
                    {
                        "snes" => "SNES",
                        "genesis" => "Genesis",
                        _ => "NES",
                    },
                    SystemId = systemId,
                    Crc32 = crc,
                    RomCrc32 = romCrc,
                    BoxArtFile = FindBoxArt(root, file, stem, crc),
                    Year = year,
                    Developer = developer,
                    Description = description,
                    Bytes = bytes,
                });
            }
        }

        games.Sort((a, b) =>
        {
            var system = string.Compare(a.System, b.System, StringComparison.Ordinal);
            return system != 0 ? system : string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
        });
        return new LibrarySnapshot
        {
            RomsPath = root,
            Games = games,
            ScannedAt = DateTimeOffset.UtcNow,
        };
    }

    private static string? FindBoxArt(string root, string romFile, string stem, string crc)
    {
        var sibling = Path.Combine(Path.GetDirectoryName(romFile) ?? root, stem + ".png");
        if (File.Exists(sibling))
        {
            return sibling;
        }

        var byName = Path.Combine(root, "boxart", stem + ".png");
        if (File.Exists(byName))
        {
            return byName;
        }

        var byCrc = Path.Combine(root, "boxart", crc + ".png");
        return File.Exists(byCrc) ? byCrc : null;
    }

    private static Dictionary<string, MetaEntry> LoadMeta(string path)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, MetaEntry>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var file = JsonSerializer.Deserialize<MetaFile>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            return (file?.Games ?? []).ToDictionary(
                entry => entry.File.Replace('\\', '/'),
                entry => entry,
                StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, MetaEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static (string Full, string? Body, long Bytes) HashRom(string path, bool nes)
    {
        using var stream = File.OpenRead(path);
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

    private sealed class MetaFile
    {
        public List<MetaEntry> Games { get; set; } = [];
    }

    private sealed class MetaEntry
    {
        public string File { get; set; } = "";
        public string? Title { get; set; }
        public string? Developer { get; set; }
        public int? Year { get; set; }
        public string? Description { get; set; }
    }
}
