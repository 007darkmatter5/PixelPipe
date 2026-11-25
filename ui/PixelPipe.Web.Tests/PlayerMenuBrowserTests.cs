using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Playwright;
using PixelPipe.Web.Services;
using Xunit;

namespace PixelPipe.Web.Tests;

public sealed class PlayerMenuBrowserTests : IAsyncLifetime
{
    private Process? _process;
    private string _url = "";
    private string _temp = "";

    public async Task InitializeAsync()
    {
        _temp = Path.Combine(Path.GetTempPath(), "pixelpipe-menu-" + Guid.NewGuid().ToString("N"));
        var roms = Path.Combine(_temp, "roms");
        Directory.CreateDirectory(roms);
        await File.WriteAllBytesAsync(Path.Combine(roms, "home.nes"), "NES\u001a"u8.ToArray());
        var port = FreePort();
        _url = $"http://127.0.0.1:{port}";
        var dll = typeof(UpdateService).Assembly.Location;
        var start = new ProcessStartInfo("dotnet", $"\"{dll}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["ASPNETCORE_URLS"] = _url;
        start.Environment["PIXELPIPE_DATA"] = Path.Combine(_temp, "data");
        start.Environment["PixelPipe__RomsPath"] = roms;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        _process = Process.Start(start) ?? throw new InvalidOperationException("Could not start PixelPipe.Web.");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (var i = 0; i < 40; i++)
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException(await Output());
            }

            try
            {
                var response = await client.GetAsync(_url + "/setup");
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch
            {
                // The host is still binding.
            }

            await Task.Delay(500);
        }

        throw new TimeoutException(await Output());
    }

    public Task DisposeAsync()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The test host is already gone.
        }

        try
        {
            if (Directory.Exists(_temp))
            {
                Directory.Delete(_temp, recursive: true);
            }
        }
        catch
        {
            // Temp files can wait for the process handle to close.
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Menu_button_is_visible_and_quit_returns_to_the_library()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.GotoAsync(_url + "/setup");
        await page.Locator("input[name=username]").FillAsync("ada");
        await page.Locator("input[name=password]").FillAsync("correct-horse");
        await page.Locator("input[name=confirm]").FillAsync("correct-horse");
        await page.Locator("button[type=submit]").ClickAsync();
        await page.WaitForURLAsync(url => !url.Contains("/setup", StringComparison.Ordinal));

        await page.GotoAsync(_url + "/play/home.nes");
        var menu = page.Locator("#pp-menu-toggle");
        await menu.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        // A Blazor re-render used to delete a button that script had inserted.
        await page.WaitForTimeoutAsync(1500);
        Assert.True(await menu.IsVisibleAsync());
        await menu.ClickAsync();
        await page.Locator("#pp-menu.pp-open").WaitForAsync();
        await page.Locator("#pp-menu [data-pp-action='quit']").ClickAsync();
        await page.WaitForURLAsync(url => url.TrimEnd('/').Equals(_url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
            || url.EndsWith("/", StringComparison.Ordinal));
        Assert.DoesNotContain("/play/", page.Url, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> Output()
    {
        if (_process is null)
        {
            return "";
        }

        var stdout = _process.StandardOutput.ReadToEndAsync();
        var stderr = _process.StandardError.ReadToEndAsync();
        await Task.WhenAll(stdout, stderr);
        return stdout.Result + stderr.Result;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
