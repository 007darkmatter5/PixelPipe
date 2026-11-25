using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using PixelPipe.UpdateCore;

const int Port = 5077;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "PixelPipeUpdater");
builder.Services.AddHostedService<UpdaterWorker>();
await builder.Build().RunAsync();

sealed class UpdaterWorker : BackgroundService
{
    private readonly IHostApplicationLifetime _lifetime;

    public UpdaterWorker(IHostApplicationLifetime lifetime)
    {
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(UpdaterLog.DataDirectory());
        var token = EnsureToken();
        UpdaterLog.Write("PixelPipe Updater service started");
        ClearManualStopOnBoot();
        await EnsureAppsAsync(stoppingToken);

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        listener.Start();
        UpdaterLog.Write($"listening on 127.0.0.1:{Port}");

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        var listen = ListenAsync(listener, token, stoppingToken);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (!File.Exists(Path.Combine(UpdaterLog.DataDirectory(), "manual-stop")))
                {
                    await EnsureAppsAsync(stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The service is stopping so the installer can replace its files.
        }

        listener.Stop();
        await listen;
    }

    private async Task ListenAsync(HttpListener listener, string token, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(stoppingToken);
            }
            catch (Exception) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                UpdaterLog.Write("listener: " + ex.Message);
                continue;
            }

            _ = Task.Run(() => Handle(context, token), stoppingToken);
        }
    }

    private void Handle(HttpListenerContext context, string token)
    {
        try
        {
            var remote = context.Request.RemoteEndPoint?.Address;
            if (remote is null || !IPAddress.IsLoopback(remote))
            {
                Write(context, 403, new { error = "Only this PC can request an update." });
                return;
            }

            var header = context.Request.Headers["Authorization"] ?? "";
            const string prefix = "Bearer ";
            var presented = header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? header[prefix.Length..].Trim()
                : "";
            if (!FixedEquals(presented, token))
            {
                Write(context, 401, new { error = "The updater token was rejected." });
                return;
            }

            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (context.Request.HttpMethod == "GET" && path == "/status")
            {
                var statusPath = Path.Combine(UpdaterLog.DataDirectory(), "updater-status.json");
                var json = File.Exists(statusPath) ? File.ReadAllText(statusPath) : """{"phase":"idle","message":"Idle."}""";
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(json);
                context.Response.OutputStream.Write(bytes);
                context.Response.Close();
                return;
            }

            if (context.Request.HttpMethod != "POST" || path != "/install")
            {
                Write(context, 404, new { error = "Not found." });
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream);
            var body = reader.ReadToEnd();
            var request = JsonSerializer.Deserialize<InstallRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (request is null || string.IsNullOrWhiteSpace(request.InstallerPath))
            {
                Write(context, 400, new { error = "The installer path is missing." });
                return;
            }

            var error = VerifyAndLaunch(request);
            if (error is not null)
            {
                UpdaterLog.WriteStatus("failed", error, null, error);
                Write(context, 400, new { error });
                return;
            }

            Write(context, 202, new { accepted = true });
            UpdaterLog.Write("installer launched; exiting so its files can be replaced");
            _lifetime.StopApplication();
            _ = Task.Run(async () =>
            {
                await Task.Delay(400);
                Environment.Exit(0);
            });
        }
        catch (Exception ex)
        {
            UpdaterLog.WriteStatus("failed", ex.Message, null, ex.Message);
            try
            {
                Write(context, 500, new { error = ex.Message });
            }
            catch
            {
                // The client may already have gone away.
            }
        }
    }

    private static string? VerifyAndLaunch(InstallRequest request)
    {
        if (!ReleaseSource.TryGetChecksumUrl(request.SourceUrl, out var checksumUrl))
        {
            return "The installer URL is not a PixelPipe GitHub release.";
        }

        var path = request.InstallerPath;
        if (!File.Exists(path))
        {
            return "The installer file is missing.";
        }

        var actual = InstallerHash.Compute(path);
        if (!InstallerHash.EqualsHash(actual, request.Sha256))
        {
            return "The installer does not match the checksum it was given.";
        }

        UpdaterLog.WriteStatus("verifying", "Downloading the release checksum.", null, null);
        string remote;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var http = new HttpRequestMessage(HttpMethod.Get, checksumUrl);
            http.Headers.UserAgent.ParseAdd("PixelPipe");
            using var response = client.Send(http);
            response.EnsureSuccessStatusCode();
            remote = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return "Could not download the release checksum: " + ex.Message;
        }

        if (!InstallerHash.EqualsHash(actual, remote))
        {
            return "The installer does not match the checksum published with that GitHub release.";
        }

        var signature = Authenticode.Check(path);
        if (signature == SignatureState.Invalid)
        {
            return "The installer has an Authenticode signature that Windows does not trust.";
        }

        UpdaterLog.Write(signature == SignatureState.Signed
            ? "Authenticode signature is valid."
            : "Installer is unsigned. The GitHub checksum matched, so the install will continue.");

        UpdaterLog.WriteStatus("installing", "Running the installer. No permission prompt is required.", null, null);
        try
        {
            File.Delete(Path.Combine(UpdaterLog.DataDirectory(), "manual-stop"));
        }
        catch
        {
            // The restart after install should still happen.
        }

        var arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS";
        var script = Path.Combine(UpdaterLog.DataDirectory(), "run-update.cmd");
        File.WriteAllText(script, $"@echo off{Environment.NewLine}\"{path}\" {arguments}{Environment.NewLine}");
        var task = "PixelPipeUpdaterRun";
        Run("schtasks.exe", $"/Delete /TN {task} /F");
        var created = Run("schtasks.exe", $"/Create /F /TN {task} /TR \"{script}\" /SC ONCE /ST 00:00 /RL HIGHEST /RU SYSTEM");
        if (created != 0)
        {
            return "Could not schedule the installer to run as Windows itself.";
        }

        var started = Run("schtasks.exe", $"/Run /TN {task}");
        if (started != 0)
        {
            return "Could not start the scheduled installer.";
        }

        return null;
    }

    private static int Run(string file, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = file,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        process?.WaitForExit(20000);
        return process?.ExitCode ?? 1;
    }

    private static async Task EnsureAppsAsync(CancellationToken cancellationToken)
    {
        var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!PortOpen(9090))
        {
            Start(Path.Combine(dir, "pixelpipe-engine.exe"), "--no-ui", null);
            UpdaterLog.Write("started the engine");
        }

        if (!PortOpen(8080))
        {
            Start(Path.Combine(dir, "PixelPipe.Web.exe"), "", new Dictionary<string, string>
            {
                ["ASPNETCORE_URLS"] = "http://0.0.0.0:8080",
                ["PixelPipe__EngineUrl"] = "http://127.0.0.1:9090",
                ["ASPNETCORE_ENVIRONMENT"] = "Production",
            });
            UpdaterLog.Write("started the web app");
            UpdaterLog.WriteStatus("restarting", "Starting PixelPipe.", null, null);
        }

        await Task.Delay(200, cancellationToken);
    }

    private static void Start(string file, string arguments, IDictionary<string, string>? env)
    {
        if (!File.Exists(file))
        {
            UpdaterLog.Write("missing " + file);
            return;
        }

        var start = new ProcessStartInfo
        {
            FileName = file,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(file)!,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (env is not null)
        {
            foreach (var pair in env)
            {
                start.Environment[pair.Key] = pair.Value;
            }
        }

        Process.Start(start);
    }

    private static bool PortOpen(int port)
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            var task = tcp.ConnectAsync("127.0.0.1", port);
            return task.Wait(300) && tcp.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static string EnsureToken()
    {
        var path = Path.Combine(UpdaterLog.DataDirectory(), "updater.token");
        if (!File.Exists(path))
        {
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            File.WriteAllText(path, token);
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "icacls.exe",
                    Arguments = $"\"{path}\" /grant *S-1-5-32-545:R /inheritance:r",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                })?.WaitForExit(8000);
            }
            catch (Exception ex)
            {
                UpdaterLog.Write("could not set the token permissions: " + ex.Message);
            }
        }

        return File.ReadAllText(path).Trim();
    }

    private static void ClearManualStopOnBoot()
    {
        // A boot is a new session. A Quit from the tray only lasts until then.
    }

    private static bool FixedEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static void Write(HttpListenerContext context, int status, object body)
    {
        var json = JsonSerializer.Serialize(body);
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.OutputStream.Write(bytes);
        context.Response.Close();
    }

    private sealed record InstallRequest(string? InstallerPath, string? Sha256, string? SourceUrl);
}

enum SignatureState
{
    Unsigned,
    Signed,
    Invalid,
}

static class Authenticode
{
    public static SignatureState Check(string path)
    {
        try
        {
            var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            return cert.Verify() ? SignatureState.Signed : SignatureState.Invalid;
        }
        catch (CryptographicException)
        {
            return SignatureState.Unsigned;
        }
    }
}
