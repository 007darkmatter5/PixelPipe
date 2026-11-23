using System.Text.Json;
using PixelPipe.Web.Models;

namespace PixelPipe.Web.Services;

public sealed class LibraryService
{
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly object _gate = new();
    private LibrarySnapshot? _cache;
    private string? _romsOverride;

    public LibraryService(IConfiguration config, IWebHostEnvironment env)
    {
        _config = config;
        _env = env;
    }

    public string RomsPath
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_romsOverride))
            {
                return _romsOverride;
            }

            var fromFile = ReadSavedRoms();
            if (!string.IsNullOrWhiteSpace(fromFile))
            {
                return fromFile;
            }

            var configured = _config["PixelPipe:RomsPath"] ?? "roms";
            // Keep UNC and other absolute paths even when the share is briefly unreachable.
            if (Path.IsPathRooted(configured) || configured.StartsWith(@"\\", StringComparison.Ordinal))
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

    private static string? ReadSavedRoms()
    {
        var file = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "PixelPipe",
            "config.json");
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("roms", out var roms))
            {
                return null;
            }

            var value = roms.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    public void UseRoms(string path)
    {
        lock (_gate)
        {
            _romsOverride = path;
            _cache = null;
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

    public string? BundledPath
    {
        get
        {
            var installed = Path.Combine(_env.ContentRootPath, "demo-roms");
            return Directory.Exists(installed) ? installed : null;
        }
    }

    private LibrarySnapshot Scan()
    {
        var root = RomsPath;
        var games = Collect(root, root, "");
        var bundled = BundledPath;
        if (bundled is not null)
        {
            games.AddRange(Collect(bundled, bundled, "bundled"));
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

    private static List<GameEntry> Collect(string libraryRoot, string scanRoot, string prefix)
    {
        var meta = LoadMeta(Path.Combine(scanRoot, "library.json"));
        var games = new List<GameEntry>();
        foreach (var found in RomScanner.Scan(scanRoot, prefix))
        {
            var metaKey = string.IsNullOrEmpty(prefix)
                ? found.RelativePath
                : found.RelativePath[(prefix.Length + 1)..];
            var hashIndex = metaKey.IndexOf('#');
            if (hashIndex >= 0)
            {
                metaKey = metaKey[..hashIndex];
            }

            var title = found.Title;
            int? year = null;
            string? developer = null;
            string? description = null;
            if (meta.TryGetValue(metaKey, out var entry))
            {
                if (!string.IsNullOrWhiteSpace(entry.Title))
                {
                    title = entry.Title!;
                }

                year = entry.Year;
                developer = entry.Developer;
                description = entry.Description;
            }

            var stem = Path.GetFileNameWithoutExtension(found.FullPath);
            games.Add(new GameEntry
            {
                RelativePath = found.RelativePath,
                Title = title,
                System = found.SystemId switch
                {
                    "snes" => "SNES",
                    "genesis" => "Genesis",
                    _ => "NES",
                },
                SystemId = found.SystemId,
                Crc32 = found.Crc32,
                RomCrc32 = found.RomCrc32,
                ContentHash = found.ContentHash,
                BoxArtFile = FindBoxArt(libraryRoot, found.FullPath, stem, found.Crc32),
                Year = year,
                Developer = developer,
                Description = description,
                Bytes = found.Bytes,
            });
        }

        return games;
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
