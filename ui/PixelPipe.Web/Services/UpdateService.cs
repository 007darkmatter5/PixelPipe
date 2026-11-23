using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace PixelPipe.Web.Services;

public sealed class UpdateService : BackgroundService
{
    public const string PrivateRepoMessage =
        "Can't reach releases: repo is private; add a token or make the repo public";

    private const string Repo = "007darkmatter5/PixelPipe";
    private readonly ILogger<UpdateService> _log;
    private readonly IHttpClientFactory _http;
    private readonly object _gate = new();
    private UpdateSnapshot _snapshot;

    public UpdateService(ILogger<UpdateService> log, IHttpClientFactory http)
    {
        _log = log;
        _http = http;
        _snapshot = new UpdateSnapshot
        {
            CurrentVersion = CurrentVersionString(),
            IncludePreviews = ReadIncludePreviews(),
            TokenConfigured = TokenStore.HasToken(),
            CanSelfUpdate = CanSelfUpdate(),
        };
    }

    public UpdateSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot with { };
            }
        }
    }

    public void SaveSettings(bool includePreviews, string? token, bool clearToken)
    {
        var dir = DataDir();
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "update-settings.json"),
            JsonSerializer.Serialize(new Dictionary<string, bool> { ["includePreviews"] = includePreviews }));
        if (clearToken)
        {
            TokenStore.Clear();
        }
        else if (!string.IsNullOrWhiteSpace(token))
        {
            TokenStore.Save(token.Trim());
        }

        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                IncludePreviews = includePreviews,
                TokenConfigured = TokenStore.HasToken(),
            };
        }
    }

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        Set(snapshot => snapshot with { Phase = "checking", Error = null });
        try
        {
            var releases = await FetchReleasesAsync(cancellationToken);
            var current = AppVersion.TryParse(CurrentVersionString(), out var parsed)
                ? parsed
                : new AppVersion(0, 0, 0, null);
            var include = Snapshot.IncludePreviews;
            var newer = ReleaseSelection.NewerThan(releases, current, include);
            Set(snapshot => snapshot with
            {
                Phase = "idle",
                Error = null,
                LatestVersion = newer?.Tag,
                ReleaseNotes = newer?.Notes,
                ReleaseUrl = newer?.HtmlUrl,
                UpdateAvailable = newer is not null,
                ExeAssetId = newer?.ExeAssetId,
                ShaAssetId = newer?.ShaAssetId,
                ExeName = newer?.ExeName,
            });
        }
        catch (UpdateAccessException ex)
        {
            Set(snapshot => snapshot with { Phase = "idle", Error = ex.Message, UpdateAvailable = false });
        }
        catch (Exception ex)
        {
            _log.LogWarning("Update check failed: {Message}", ex.Message);
            Set(snapshot => snapshot with { Phase = "idle", Error = ex.Message, UpdateAvailable = false });
        }
    }

    public async Task ApplyAsync(bool confirmed, bool localCaller, CancellationToken cancellationToken)
    {
        var snapshot = Snapshot;
        if (!snapshot.CanSelfUpdate)
        {
            throw new InvalidOperationException("Docker installs update by pulling a new image: docker compose pull && docker compose up -d");
        }

        if (!localCaller && !confirmed)
        {
            throw new InvalidOperationException("Confirm the update from this browser before it can run.");
        }

        if (!snapshot.UpdateAvailable || snapshot.ExeAssetId is null || snapshot.ShaAssetId is null || snapshot.LatestVersion is null)
        {
            throw new InvalidOperationException("No update is ready to install.");
        }

        var dir = Path.Combine(DataDir(), "updates");
        Directory.CreateDirectory(dir);
        var fileName = Path.GetFileName(snapshot.ExeName ?? "");
        if (string.IsNullOrWhiteSpace(fileName) || !SetupAssets.IsInstaller(fileName))
        {
            fileName = "PixelPipe-Setup.exe";
        }

        var setupPath = Path.Combine(dir, fileName);
        Set(s => s with { Phase = "downloading" });
        var setup = await DownloadAssetAsync(snapshot.ExeAssetId.Value, cancellationToken);
        var shaText = await DownloadAssetTextAsync(snapshot.ShaAssetId.Value, cancellationToken);
        Set(s => s with { Phase = "verifying" });
        if (!ChecksumFile.Matches(setup, shaText))
        {
            Set(s => s with { Phase = "idle", Error = "The downloaded installer did not match its SHA256 file." });
            throw new InvalidOperationException("Checksum mismatch.");
        }

        await File.WriteAllBytesAsync(setupPath, setup, cancellationToken);
        Set(s => s with { Phase = "installing" });
        StartInstaller(setupPath);
    }

    public static bool CanSelfUpdate() =>
        OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PIXELPIPE_DOCKER"));

    public static string CurrentVersionString()
    {
        var info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info))
        {
            return "0.3.0";
        }

        var plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await CheckAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromHours(12), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task<IReadOnlyList<RemoteRelease>> FetchReleasesAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.github.com/repos/{Repo}/releases?per_page=30");
        AddGitHubHeaders(request, assetDownload: false);
        var client = _http.CreateClient("github");
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Unauthorized)
        {
            throw new UpdateAccessException(PrivateRepoMessage);
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseReleases(json);
    }

    public static IReadOnlyList<RemoteRelease> ParseReleases(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<RemoteRelease>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("draft", out var draft) && draft.GetBoolean())
            {
                continue;
            }

            var tag = item.GetProperty("tag_name").GetString() ?? "";
            if (!AppVersion.TryParse(tag, out var version))
            {
                continue;
            }

            long? exe = null;
            long? sha = null;
            string? exeName = null;
            if (item.TryGetProperty("assets", out var assets))
            {
                var installers = new List<(string Name, long Id)>();
                var checksums = new List<(string Name, long Id)>();
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    var id = asset.GetProperty("id").GetInt64();
                    if (SetupAssets.IsInstaller(name))
                    {
                        installers.Add((name, id));
                    }
                    else if (name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
                    {
                        checksums.Add((name, id));
                    }
                }

                var chosen = SetupAssets.PreferInstaller(installers);
                if (chosen is { } setup)
                {
                    exe = setup.Id;
                    exeName = setup.Name;
                    var checksum = checksums.FirstOrDefault(asset =>
                        asset.Name.Equals(setup.Name + ".sha256", StringComparison.OrdinalIgnoreCase));
                    if (checksum.Name is null)
                    {
                        checksum = checksums.FirstOrDefault(asset =>
                            asset.Name.Equals("PixelPipe-Setup.exe.sha256", StringComparison.OrdinalIgnoreCase));
                    }

                    if (checksum.Name is not null)
                    {
                        sha = checksum.Id;
                    }
                }
            }

            list.Add(new RemoteRelease(
                tag,
                version,
                item.TryGetProperty("prerelease", out var pre) && pre.GetBoolean(),
                item.TryGetProperty("body", out var body) ? body.GetString() : null,
                item.TryGetProperty("html_url", out var url) ? url.GetString() : null,
                exe,
                sha,
                exeName));
        }

        return list;
    }

    private async Task<byte[]> DownloadAssetAsync(long id, CancellationToken cancellationToken)
    {
        using var request = AssetRequest(id);
        var client = _http.CreateClient("github");
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Unauthorized)
        {
            throw new UpdateAccessException(PrivateRepoMessage);
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private async Task<string> DownloadAssetTextAsync(long id, CancellationToken cancellationToken)
    {
        var bytes = await DownloadAssetAsync(id, cancellationToken);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static HttpRequestMessage AssetRequest(long id)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.github.com/repos/{Repo}/releases/assets/{id}");
        AddGitHubHeaders(request, assetDownload: true);
        return request;
    }

    private static void AddGitHubHeaders(HttpRequestMessage request, bool assetDownload)
    {
        request.Headers.UserAgent.ParseAdd("PixelPipe");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
            assetDownload ? "application/octet-stream" : "application/vnd.github+json"));
        var token = TokenStore.Load();
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    private static void StartInstaller(string setupPath)
    {
        var tray = Path.Combine(InstallDir(), "PixelPipe.Tray.exe");
        var modePath = Path.Combine(DataDir(), "launch-mode.txt");
        var script = Path.Combine(DataDir(), "updates", "apply-update.ps1");
        var body = $$"""
            $ErrorActionPreference = 'Continue'
            Start-Sleep -Seconds 2
            $proc = Start-Process -FilePath '{{setupPath.Replace("'", "''")}}' -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CLOSEAPPLICATIONS' -Wait -PassThru
            if (Test-Path '{{modePath.Replace("'", "''")}}') {
              $mode = (Get-Content '{{modePath.Replace("'", "''")}}' -Raw).Trim()
              if ($mode -eq 'tray' -and (Test-Path '{{tray.Replace("'", "''")}}')) {
                Start-Process -FilePath '{{tray.Replace("'", "''")}}'
              }
            }
            exit $proc.ExitCode
            """;
        File.WriteAllText(script, body);
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    private static string InstallDir() =>
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string DataDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PixelPipe");

    private static bool ReadIncludePreviews()
    {
        try
        {
            var path = Path.Combine(DataDir(), "update-settings.json");
            if (!File.Exists(path))
            {
                return true;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return !doc.RootElement.TryGetProperty("includePreviews", out var value) || value.GetBoolean();
        }
        catch
        {
            return true;
        }
    }

    private void Set(Func<UpdateSnapshot, UpdateSnapshot> change)
    {
        lock (_gate)
        {
            _snapshot = change(_snapshot);
        }
    }
}

public sealed record UpdateSnapshot
{
    public string CurrentVersion { get; init; } = "0.3.0";
    public string? LatestVersion { get; init; }
    public string? ReleaseNotes { get; init; }
    public string? ReleaseUrl { get; init; }
    public bool UpdateAvailable { get; init; }
    public bool IncludePreviews { get; init; } = true;
    public bool TokenConfigured { get; init; }
    public bool CanSelfUpdate { get; init; }
    public string? Error { get; init; }
    public string Phase { get; init; } = "idle";
    public long? ExeAssetId { get; init; }
    public long? ShaAssetId { get; init; }
    public string? ExeName { get; init; }
}

public sealed class UpdateAccessException : Exception
{
    public UpdateAccessException(string message) : base(message)
    {
    }
}

public static class TokenStore
{
    private static string PathName => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "PixelPipe",
        "github-token.bin");

    public static bool HasToken() => File.Exists(PathName);

    public static void Save(string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        var bytes = System.Text.Encoding.UTF8.GetBytes(token);
        var stored = OperatingSystem.IsWindows()
            ? ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)
            : bytes;
        File.WriteAllBytes(PathName, stored);
    }

    public static string? Load()
    {
        if (!File.Exists(PathName))
        {
            return null;
        }

        var stored = File.ReadAllBytes(PathName);
        var bytes = OperatingSystem.IsWindows()
            ? ProtectedData.Unprotect(stored, null, DataProtectionScope.CurrentUser)
            : stored;
        var token = System.Text.Encoding.UTF8.GetString(bytes);
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    public static void Clear()
    {
        if (File.Exists(PathName))
        {
            File.Delete(PathName);
        }
    }
}
