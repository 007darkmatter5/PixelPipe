using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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
        menu.Items.Add("Reset admin password…", null, (_, _) => ResetAdminPassword());
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
        OfferRomFolder();
        RememberUnc();
        StartChildren();
        AnnounceUpdated();
        _ = WatchUpdates();
        _ = WatchUpdateProgress();
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
        ShowProgress("Starting the update…", 0, false);
        Toast("PixelPipe update", "Downloading the installer.");
        UpdateLog("starting update from the tray");
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using var body = new StringContent("{\"confirm\":true}", System.Text.Encoding.UTF8, "application/json");
            await client.PostAsync("http://127.0.0.1:8080/api/app/update/apply", body);
            Toast("PixelPipe update", "Installing. Windows will ask for permission.");
            ShowProgress("Installing. Windows will ask for permission.", 100, false);
        }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
            UpdateLog(ex.Message);
            Toast("PixelPipe update failed", ex.Message);
        }
    }

    private async Task WatchUpdateProgress()
    {
        string? lastPhase = null;
        while (!_quitting)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            var quit = Path.Combine(_dataDir, "quit.request");
            if (File.Exists(quit))
            {
                try
                {
                    File.Delete(quit);
                }
                catch
                {
                    // The installer removes the file if this process is killed first.
                }

                _ui.BeginInvoke(new Action(Quit));
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                using var response = await client.GetAsync("http://127.0.0.1:8080/api/app/update");
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                var phase = root.TryGetProperty("phase", out var phaseValue) ? phaseValue.GetString() : "idle";
                var done = root.TryGetProperty("bytesDownloaded", out var doneValue) ? doneValue.GetInt64() : 0;
                var total = root.TryGetProperty("bytesTotal", out var totalValue) && totalValue.ValueKind == JsonValueKind.Number
                    ? totalValue.GetInt64()
                    : (long?)null;
                var percent = total is > 0 ? (int)Math.Min(100, done * 100 / total.Value) : 0;
                _ui.BeginInvoke(new Action(() =>
                {
                    if (phase is "downloading" or "verifying" or "installing" or "checking")
                    {
                        var text = phase switch
                        {
                            "downloading" => total is > 0
                                ? $"Downloading {done / 1048576.0:0.0} MB of {total.Value / 1048576.0:0.0} MB"
                                : $"Downloading {done / 1048576.0:0.0} MB",
                            "verifying" => "Verifying the checksum…",
                            "installing" => "Installing. Windows will ask for permission.",
                            _ => "Checking for the update…",
                        };
                        ShowProgress(text, percent, phase == "downloading" && total is not > 0);
                        _icon.Text = TrimTip($"PixelPipe — {text}");
                        _icon.Icon = SystemIcons.Information;
                        if (phase != lastPhase)
                        {
                            Toast("PixelPipe update", text);
                            UpdateLog(text);
                            lastPhase = phase;
                        }
                    }
                    else if (lastPhase is not null)
                    {
                        HideProgress();
                        _icon.Text = "PixelPipe";
                        _icon.Icon = SystemIcons.Application;
                        lastPhase = null;
                    }
                }));
            }
            catch
            {
                // The UI is restarting.
            }
        }
    }

    private void AnnounceUpdated()
    {
        try
        {
            var path = Path.Combine(_dataDir, "update-notice.json");
            if (!File.Exists(path))
            {
                return;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var tag = doc.RootElement.TryGetProperty("tag", out var value) ? value.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag))
            {
                return;
            }

            var version = typeof(Program).Assembly.GetName().Version;
            var current = version is null ? "" : $"{version.Major}.{version.Minor}.{version.Build}";
            var bare = tag.TrimStart('v', 'V');
            var dash = bare.IndexOf('-');
            if (dash >= 0)
            {
                bare = bare[..dash];
            }

            if (!string.Equals(bare, current, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Toast("PixelPipe", $"Updated to {tag}.");
            UpdateLog($"updated to {tag}");
            _icon.Text = TrimTip($"PixelPipe — updated to {tag}");
        }
        catch
        {
            // A missing notice just means this was a normal start.
        }
    }

    private void ShowProgress(string text, int percent, bool unknown)
    {
        if (_progress is null || _progress.IsDisposed)
        {
            _progress = new UpdateProgressForm();
            _progress.Show();
        }

        _progress.Detail.Text = text;
        _progress.Bar.Style = unknown ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
        if (!unknown)
        {
            _progress.Bar.Value = percent;
        }
    }

    private void HideProgress()
    {
        if (_progress is null || _progress.IsDisposed)
        {
            return;
        }

        _progress.Close();
        _progress = null;
    }

    private void Toast(string title, string message)
    {
        _icon.ShowBalloonTip(8000, title, message, ToolTipIcon.Info);
    }

    private static string TrimTip(string text) => text.Length <= 63 ? text : text[..63];

    private void UpdateLog(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(_dataDir, "pixelpipe.log"),
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} update: {message}{Environment.NewLine}");
        }
        catch
        {
            // The tray log is optional.
        }
    }

    private UpdateProgressForm? _progress;

    private void ResetAdminPassword()
    {
        using var dialog = new ResetPasswordForm();
        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        var web = Path.Combine(_installDir, "PixelPipe.Web.exe");
        if (!File.Exists(web))
        {
            MessageBox.Show("PixelPipe.Web.exe was not found next to the tray app.", "PixelPipe");
            return;
        }

        var request = Path.Combine(Path.GetTempPath(), "pixelpipe-reset-" + Guid.NewGuid().ToString("N"));
        var result = request + ".result";
        try
        {
            File.WriteAllText(request, dialog.Username + "\n" + dialog.Password + "\n" + dialog.Password + "\n");
            MessageBox.Show("Windows will ask for permission to change the password.", "PixelPipe");
            Process.Start(new ProcessStartInfo
            {
                FileName = web,
                Arguments = "--reset-password-file \"" + request + "\"",
                UseShellExecute = true,
                Verb = "runas",
            });
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!File.Exists(result) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(300);
            }

            if (!File.Exists(result))
            {
                MessageBox.Show("The password reset did not finish. If you cancelled the permission prompt, nothing was changed.", "PixelPipe");
                return;
            }

            var text = File.ReadAllText(result).Trim();
            var ok = text.StartsWith("0", StringComparison.Ordinal);
            var message = text.Length > 2 ? text[2..].Trim() : (ok ? "Password updated. Sign in with the new password." : "The password was not changed.");
            MessageBox.Show(message, "PixelPipe");
            UpdateLog(ok ? $"reset password for {dialog.Username}" : message);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            MessageBox.Show("Windows permission was cancelled. The password was not changed.", "PixelPipe");
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not reset the password: " + ex.Message, "PixelPipe");
        }
        finally
        {
            try
            {
                if (File.Exists(request)) File.Delete(request);
                if (File.Exists(result)) File.Delete(result);
            }
            catch
            {
                // Temp files are removed on the next reboot if they stay behind.
            }
        }
    }

    private void OfferRomFolder()
    {
        var current = ReadRomsPath();
        if (!string.IsNullOrWhiteSpace(current) &&
            !current.EndsWith("demo-roms", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the ROM folder. Mapped drives such as M: are listed. You can also set a UNC path later in Settings.",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        if (dialog.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return;
        }

        SaveRoms(dialog.SelectedPath, UncFor(dialog.SelectedPath));
    }

    private void SaveRoms(string path, string? unc)
    {
        var file = Path.Combine(_dataDir, "config.json");
        JsonNode root = File.Exists(file)
            ? JsonNode.Parse(File.ReadAllText(file)) ?? new JsonObject()
            : new JsonObject();
        root["roms"] = path;
        if (string.IsNullOrWhiteSpace(unc))
        {
            root.AsObject().Remove("roms_unc");
        }
        else
        {
            root["roms_unc"] = unc;
        }

        File.WriteAllText(file, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private void RememberUnc()
    {
        var path = ReadRomsPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var unc = UncFor(path);
        if (string.IsNullOrWhiteSpace(unc))
        {
            return;
        }

        SaveRoms(path, unc);
    }

    private static string? UncFor(string path)
    {
        if (path.Length < 2 || path[1] != ':')
        {
            return null;
        }

        var buffer = new StringBuilder(512);
        var length = buffer.Capacity;
        if (WNetGetConnection(path[..2], buffer, ref length) != 0)
        {
            return null;
        }

        var remote = buffer.ToString().TrimEnd('\\');
        var rest = path.Length > 2 ? path[2..].TrimStart('\\') : "";
        return string.IsNullOrEmpty(rest) ? remote : remote + "\\" + rest;
    }

    [System.Runtime.InteropServices.DllImport("mpr.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int WNetGetConnection(string localName, StringBuilder buffer, ref int length);

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

    private sealed class UpdateProgressForm : Form
    {
        public Label Detail { get; }
        public ProgressBar Bar { get; }

        public UpdateProgressForm()
        {
            Text = "PixelPipe update";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ControlBox = false;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            Width = 440;
            Height = 150;
            Detail = new Label { Left = 16, Top = 16, Width = 390, Height = 48 };
            Bar = new ProgressBar { Left = 16, Top = 72, Width = 390, Height = 22, Minimum = 0, Maximum = 100 };
            Controls.Add(Detail);
            Controls.Add(Bar);
        }
    }

    private sealed class ResetPasswordForm : Form
    {
        private readonly TextBox _user = new() { Left = 16, Top = 28, Width = 380 };
        private readonly TextBox _password = new() { Left = 16, Top = 76, Width = 380, UseSystemPasswordChar = true };
        private readonly TextBox _confirm = new() { Left = 16, Top = 124, Width = 380, UseSystemPasswordChar = true };

        public string Username => _user.Text.Trim();
        public string Password => _password.Text;

        public ResetPasswordForm()
        {
            Text = "Reset admin password";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Width = 430;
            Height = 280;
            Controls.Add(new Label { Left = 16, Top = 8, Width = 380, Text = "Username" });
            Controls.Add(_user);
            Controls.Add(new Label { Left = 16, Top = 56, Width = 380, Text = "New password" });
            Controls.Add(_password);
            Controls.Add(new Label { Left = 16, Top = 104, Width = 380, Text = "Confirm password" });
            Controls.Add(_confirm);
            var show = new CheckBox { Left = 16, Top = 156, Width = 200, Text = "Show password" };
            show.CheckedChanged += (_, _) =>
            {
                _password.UseSystemPasswordChar = !show.Checked;
                _confirm.UseSystemPasswordChar = !show.Checked;
            };
            Controls.Add(show);
            var ok = new Button { Text = "Reset", Left = 210, Top = 196, Width = 90 };
            var cancel = new Button { Text = "Cancel", Left = 306, Top = 196, Width = 90, DialogResult = DialogResult.Cancel };
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(ok);
            Controls.Add(cancel);
            ok.Click += (_, _) =>
            {
                if (_password.Text.Length < 8 || _password.Text != _confirm.Text)
                {
                    MessageBox.Show(
                        _password.Text.Length < 8 ? "Password must be at least 8 characters." : "The passwords do not match.",
                        "PixelPipe");
                    return;
                }

                DialogResult = DialogResult.OK;
                Close();
            };
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
