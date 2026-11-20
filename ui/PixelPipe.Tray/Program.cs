using System.Diagnostics;
using System.Text.Json;

namespace PixelPipe.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\PixelPipe.Tray", out var created);
        if (!created)
        {
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApplication());
        GC.KeepAlive(mutex);
    }
}

/// <summary>
/// Sign-in host. The engine and web UI are started with no console window.
/// </summary>
internal sealed class TrayApplication : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly Control _ui;
    private readonly string _installDir;
    private readonly string _dataDir;
    private Process? _engine;
    private Process? _web;
    private bool _quitting;
    private int _generation;

    public TrayApplication()
    {
        _installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PixelPipe");
        Directory.CreateDirectory(_dataDir);
        _ui = new Control();
        _ui.CreateControl();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open PixelPipe", null, (_, _) => OpenSite());
        menu.Items.Add("Open log folder", null, (_, _) => OpenLogs());
        menu.Items.Add("Check for updates", null, (_, _) => _ = CheckForUpdates(announce: true));
        _updateItem = new ToolStripMenuItem("Update") { Visible = false };
        _updateItem.Click += (_, _) => _ = ApplyUpdate();
        menu.Items.Add(_updateItem);
        menu.Items.Add("Restart", null, (_, _) => Restart());
        menu.Items.Add("Quit", null, (_, _) => Quit());

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "PixelPipe",
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => OpenSite();
        StartChildren();
        _ = WatchUpdates();
    }

    private readonly ToolStripMenuItem _updateItem;
    private string? _latestVersion;

    private async Task WatchUpdates()
    {
        await Task.Delay(TimeSpan.FromSeconds(25));
        while (!_quitting)
        {
            await CheckForUpdates(announce: true);
            await Task.Delay(TimeSpan.FromHours(12));
        }
    }

    private async Task CheckForUpdates(bool announce)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var response = await client.PostAsync("http://127.0.0.1:8080/api/app/update/check", new StringContent(""));
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var available = root.TryGetProperty("updateAvailable", out var flag) && flag.GetBoolean();
            var latest = root.TryGetProperty("latestVersion", out var version) ? version.GetString() : null;
            _ui.BeginInvoke(new Action(() =>
            {
                _latestVersion = latest;
                _updateItem.Visible = available && !string.IsNullOrWhiteSpace(latest);
                _updateItem.Text = available ? $"Update to {latest}" : "Update";
                if (announce && available && !string.IsNullOrWhiteSpace(latest))
                {
                    _icon.ShowBalloonTip(8000, "PixelPipe update", $"Version {latest} is available.", ToolTipIcon.Info);
                }
            }));
        }
        catch
        {
            // The web UI may still be starting.
        }
    }

    private async Task ApplyUpdate()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using var body = new StringContent("{\"confirm\":true}", System.Text.Encoding.UTF8, "application/json");
            await client.PostAsync("http://127.0.0.1:8080/api/app/update/apply", body);
        }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
        }
    }

    private void StartChildren()
    {
        var generation = _generation;
        _engine = StartHidden(Path.Combine(_installDir, "pixelpipe-engine.exe"), "--no-ui", null);
        Watch(_engine, generation, () =>
        {
            _engine = StartHidden(Path.Combine(_installDir, "pixelpipe-engine.exe"), "--no-ui", null);
            return _engine;
        });
        var env = new Dictionary<string, string>
        {
            ["PixelPipe__EngineUrl"] = "http://127.0.0.1:9090",
            ["ASPNETCORE_URLS"] = "http://0.0.0.0:8080",
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
        };
        var roms = ReadRomsPath();
        if (!string.IsNullOrWhiteSpace(roms))
        {
            env["PixelPipe__RomsPath"] = roms;
        }

        _web = StartHidden(Path.Combine(_installDir, "PixelPipe.Web.exe"), "", env);
        Watch(_web, generation, () =>
        {
            _web = StartHidden(Path.Combine(_installDir, "PixelPipe.Web.exe"), "", env);
            return _web;
        });
    }

    private void Watch(Process? process, int generation, Func<Process?> restart)
    {
        if (process is null)
        {
            return;
        }

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            if (_quitting || generation != _generation || _ui.IsDisposed)
            {
                return;
            }

            _ui.BeginInvoke(new Action(() =>
            {
                if (_quitting || generation != _generation)
                {
                    return;
                }

                var timer = new System.Windows.Forms.Timer { Interval = 2000 };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    timer.Dispose();
                    if (!_quitting && generation == _generation)
                    {
                        Watch(restart(), generation, restart);
                    }
                };
                timer.Start();
            }));
        };
    }

    private Process? StartHidden(string file, string arguments, IDictionary<string, string>? env)
    {
        if (!File.Exists(file))
        {
            AppendLog($"missing {file}");
            return null;
        }

        var start = new ProcessStartInfo
        {
            FileName = file,
            Arguments = arguments,
            WorkingDirectory = _installDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        if (env is not null)
        {
            foreach (var pair in env)
            {
                start.Environment[pair.Key] = pair.Value;
            }
        }

        try
        {
            return Process.Start(start);
        }
        catch (Exception ex)
        {
            AppendLog(ex.ToString());
            return null;
        }
    }

    private static string? ReadRomsPath()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "PixelPipe",
                "config.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("roms", out var roms) ? roms.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private void OpenSite()
    {
        try
        {
            Process.Start(new ProcessStartInfo("http://127.0.0.1:8080") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog(ex.ToString());
        }
    }

    private void OpenLogs()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", _dataDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog(ex.ToString());
        }
    }

    private void Restart()
    {
        StopChildren();
        StartChildren();
    }

    private void Quit()
    {
        _quitting = true;
        StopChildren();
        _icon.Visible = false;
        _icon.Dispose();
        ExitThread();
    }

    private void StopChildren()
    {
        _generation++;
        Stop(_web);
        Stop(_engine);
        _web = null;
        _engine = null;
    }

    private static void Stop(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(4000);
            }
        }
        catch
        {
            // Already gone.
        }
    }

    private void AppendLog(string line)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(_dataDir, "pixelpipe-tray.log"),
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch
        {
            // The tray still has to stay up if the log folder is locked.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _quitting = true;
            StopChildren();
            _icon.Dispose();
            _ui.Dispose();
        }

        base.Dispose(disposing);
    }
}
