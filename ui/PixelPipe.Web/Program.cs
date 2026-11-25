using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using PixelPipe.Web.Services;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Transforms;

if (TryParseResetArgs(args, out var resetUser, out var resetFromStdin))
{
    Environment.Exit(ResetPassword(resetUser, resetFromStdin));
}

AppPaths.EnsureDataDirectory();

var builder = WebApplication.CreateBuilder(args);
AddFileLog(builder.Logging);

static void AddFileLog(ILoggingBuilder logging)
{
    try
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "PixelPipe");
        Directory.CreateDirectory(dir);
        logging.AddProvider(new PixelPipe.Web.AppendFileLogger(Path.Combine(dir, "pixelpipe-web.log")));
    }
    catch
    {
        // A read-only machine can still log to the console.
    }
}

var keyDir = Path.Combine(AppPaths.DataDirectory(), "keys");
Directory.CreateDirectory(keyDir);
var dataProtection = builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyDir))
    .SetApplicationName("PixelPipe");
if (OperatingSystem.IsWindows())
{
    dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);
}

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddMudServices();
builder.Services.AddSingleton<LibraryService>();
builder.Services.AddSingleton<AccountStore>();
builder.Services.AddSingleton<TurnClient>();
builder.Services.AddSingleton<SessionGateway>();
builder.Services.AddHttpForwarder();
builder.Services.AddHttpClient("turn", client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "PixelPipe.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
    });
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "PixelPipe.Antiforgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("signin", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});
builder.Services.AddHttpClient("engine", (sp, client) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var url = config["PixelPipe:EngineUrl"] ?? "http://127.0.0.1:9090";
    client.BaseAddress = new Uri(url.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(8);
});
builder.Services.AddSingleton<EngineClient>();
builder.Services.AddHttpClient("github", client => client.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddSingleton<UpdateService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UpdateService>());

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.Use(async (context, next) =>
{
    var peer = context.Connection.RemoteIpAddress;
    context.Items["pixelpipe-peer"] = peer;
    var site = SiteSettings.Load();
    if (ProxyTrust.IsTrustedPeer(peer, site.BehindProxy))
    {
        if (context.Request.Headers.TryGetValue("X-Forwarded-For", out var forwardedFor))
        {
            var first = forwardedFor.ToString().Split(',')[0].Trim();
            if (System.Net.IPAddress.TryParse(first, out var address))
            {
                context.Connection.RemoteIpAddress = address;
            }
        }

        if (context.Request.Headers.TryGetValue("X-Forwarded-Proto", out var proto))
        {
            var scheme = proto.ToString().Split(',')[0].Trim();
            if (scheme is "http" or "https")
            {
                context.Request.Scheme = scheme;
            }
        }

        if (context.Request.Headers.TryGetValue("X-Forwarded-Host", out var host))
        {
            var forwardedHost = host.ToString().Split(',')[0].Trim();
            if (!string.IsNullOrWhiteSpace(forwardedHost))
            {
                context.Request.Host = new HostString(forwardedHost);
            }
        }
    }

    await next();
    if (context.Response.StatusCode == StatusCodes.Status400BadRequest
        && (context.Request.Path == "/login" || context.Request.Path == "/setup")
        && !context.Response.HasStarted)
    {
        var target = context.Request.Path + "?error=" + Uri.EscapeDataString(
            "The sign-in form expired or the security cookie was rejected. Refresh the page and try again.");
        context.Response.Redirect(target);
    }
});
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    if (path.StartsWithSegments("/setup")
        || path.StartsWithSegments("/_framework")
        || path.StartsWithSegments("/_content")
        || path.StartsWithSegments("/_blazor"))
    {
        await next();
        return;
    }

    var accounts = context.RequestServices.GetRequiredService<AccountStore>();
    if (!accounts.Any())
    {
        context.Response.Redirect("/setup");
        return;
    }

    await next();
});

app.MapGet("/login", (HttpContext http, IAntiforgery antiforgery, AccountStore accounts) =>
{
    if (!accounts.Any())
    {
        return Results.Redirect("/setup");
    }

    var tokens = antiforgery.GetAndStoreTokens(http);
    return Results.Content(AuthPages.Login(tokens.RequestToken ?? "", http.Request.Query["error"]), "text/html");
}).AllowAnonymous();
app.MapPost("/login", async (HttpContext http, IAntiforgery antiforgery, AccountStore accounts) =>
{
    try
    {
        await antiforgery.ValidateRequestAsync(http);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.Redirect("/login?error=" + Uri.EscapeDataString(
            "The sign-in form expired or the security cookie was rejected. Refresh the page and try again."));
    }

    var form = await http.Request.ReadFormAsync();
    var check = accounts.CheckPassword(form["username"].ToString(), form["password"].ToString());
    if (!check.Ok || check.User is null)
    {
        return Results.Redirect("/login?error=" + Uri.EscapeDataString(check.Error ?? "Sign-in failed."));
    }

    var remember = form["remember"] == "1";
    await SignInAsync(http, check.User, remember);
    return Results.Redirect("/");
}).AllowAnonymous().RequireRateLimiting("signin");
app.MapGet("/setup", (HttpContext http, IAntiforgery antiforgery, AccountStore accounts) =>
{
    if (accounts.Any())
    {
        return Results.Redirect("/login");
    }

    var tokens = antiforgery.GetAndStoreTokens(http);
    return Results.Content(AuthPages.Setup(tokens.RequestToken ?? "", http.Request.Query["error"]), "text/html");
}).AllowAnonymous();
app.MapPost("/setup", async (HttpContext http, IAntiforgery antiforgery, AccountStore accounts, LibraryService library) =>
{
    if (accounts.Any())
    {
        return Results.Redirect("/");
    }

    try
    {
        await antiforgery.ValidateRequestAsync(http);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.Redirect("/setup?error=" + Uri.EscapeDataString(
            "The form expired or the security cookie was rejected. Refresh the page and try again."));
    }

    var form = await http.Request.ReadFormAsync();
    var mismatch = PasswordRules.Check(form["password"], form["confirm"]);
    if (mismatch is not null)
    {
        return Results.Redirect("/setup?error=" + Uri.EscapeDataString(mismatch));
    }

    AccountUser admin;
    try
    {
        admin = accounts.Create(form["username"].ToString(), form["password"].ToString(), "Admin");
    }
    catch (Exception ex)
    {
        return Results.Redirect("/setup?error=" + Uri.EscapeDataString(ex.Message));
    }

    var games = library.Get().Games.Select(game => (game.Crc32, game.RomCrc32, game.SystemId, game.ContentHash));
    SaveLayout.MigrateLegacy(AppPaths.DataDirectory(), admin.Id, games);
    await SignInAsync(http, admin, false);
    return Results.Redirect("/");
}).AllowAnonymous().RequireRateLimiting("signin");
app.MapGet("/logout", (HttpContext http, IAntiforgery antiforgery) =>
{
    var tokens = antiforgery.GetAndStoreTokens(http);
    return Results.Content(AuthPages.Logout(tokens.RequestToken ?? ""), "text/html");
}).RequireAuthorization();
app.MapPost("/logout", async (HttpContext http, IAntiforgery antiforgery) =>
{
    await antiforgery.ValidateRequestAsync(http);
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).RequireAuthorization();
app.MapPost("/api/session", async (HttpContext http, SessionGateway gateway, AccountStore accounts, CancellationToken cancellationToken) =>
{
    var id = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
    var user = id is null ? null : accounts.FindById(id);
    if (user is null || user.Disabled)
    {
        return Results.Unauthorized();
    }

    var body = await http.Request.ReadFromJsonAsync<SessionStartBody>(cancellationToken) ?? new SessionStartBody("", "");
    return await gateway.StartAsync(user, body.Rom, body.Slot ?? "", cancellationToken);
}).RequireAuthorization();
var engineBase = builder.Configuration["PixelPipe:EngineUrl"] ?? "http://127.0.0.1:9090";
app.MapForwarder("/api/session/{**catch-all}", engineBase.TrimEnd('/'), new ForwarderRequestConfig(), transform =>
{
    transform.AddRequestTransform(context =>
    {
        var id = context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var role = context.HttpContext.User.FindFirstValue(ClaimTypes.Role) ?? "User";
        if (id.Length > 0)
        {
            context.ProxyRequest.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                EngineTokens.Issue(id, role, TimeSpan.FromMinutes(10)));
        }

        context.ProxyRequest.Headers.Remove("Cookie");
        return ValueTask.CompletedTask;
    });
}).RequireAuthorization();
app.MapGet("/media/boxart/{*path}", (string path, LibraryService library) =>
{
    var relative = Uri.UnescapeDataString(path);
    var game = library.Find(relative);
    if (game?.BoxArtFile is { } file && File.Exists(file))
    {
        return Results.File(file, "image/png");
    }

    return Results.Content(library.PlaceholderSvg(game, relative), "image/svg+xml");
}).RequireAuthorization();

app.MapGet("/api/library/folders", (string? path) => Results.Json(RomFolder.List(path))).RequireAuthorization();
app.MapPost("/api/library/roms", async (RomPathBody body, LibraryService library, EngineClient engine) =>
{
    if (string.IsNullOrWhiteSpace(body.Path))
    {
        return Results.BadRequest("ROM path is empty.");
    }

    var saved = RomFolder.Remember(body.Path);
    library.UseRoms(saved.Path);
    await engine.PostAsync("api/library/root", new { path = saved.Path });
    return Results.Json(new { path = saved.Path, unc = saved.Unc, games = library.Get().Games.Count });
}).RequireAuthorization(policy => policy.RequireRole("Admin"));
app.MapGet("/api/app/update", (UpdateService updates) => Results.Json(updates.Snapshot)).AddEndpointFilter(LocalOrSignedIn);
app.MapPost("/api/app/update/check", async (UpdateService updates, CancellationToken cancellationToken) =>
{
    await updates.CheckAsync(cancellationToken);
    return Results.Json(updates.Snapshot);
}).AddEndpointFilter(LocalOrSignedIn);
app.MapPost("/api/app/update/ack", (UpdateService updates) =>
{
    updates.AcknowledgeNotice();
    return Results.Json(updates.Snapshot);
}).AddEndpointFilter(LocalOrSignedIn);
app.MapGet("/api/app/update/log", () =>
{
    var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PixelPipe", "pixelpipe.log");
    if (!File.Exists(path))
    {
        return Results.Text("No update log yet.", "text/plain");
    }

    return Results.Text(File.ReadAllText(path), "text/plain");
}).AddEndpointFilter(LocalOrSignedIn);
app.MapPost("/api/app/update/settings", async (UpdateSettingsBody body, UpdateService updates, CancellationToken cancellationToken) =>
{
    updates.SaveSettings(body.IncludePreviews, body.Token, body.ClearToken);
    await updates.CheckAsync(cancellationToken);
    return Results.Json(updates.Snapshot);
}).RequireAuthorization(policy => policy.RequireRole("Admin"));
app.MapPost("/api/app/update/apply", async (HttpContext http, UpdateApplyBody body, UpdateService updates, CancellationToken cancellationToken) =>
{
    var local = http.Connection.RemoteIpAddress is null || System.Net.IPAddress.IsLoopback(http.Connection.RemoteIpAddress);
    try
    {
        await updates.ApplyAsync(body.Confirm, local, cancellationToken);
        return Results.Json(updates.Snapshot);
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message, snapshot = updates.Snapshot }, statusCode: StatusCodes.Status400BadRequest);
    }
}).AddEndpointFilter(LocalOrSignedIn);

app.MapRazorComponents<PixelPipe.Web.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();

static async ValueTask<object?> LocalOrSignedIn(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
{
    var http = context.HttpContext;
    var signedIn = http.User.Identity?.IsAuthenticated == true;
    var peer = http.Items["pixelpipe-peer"] as System.Net.IPAddress ?? http.Connection.RemoteIpAddress;
    var local = peer is null || System.Net.IPAddress.IsLoopback(peer);
    if (!signedIn && !local)
    {
        return Results.Unauthorized();
    }

    return await next(context);
}

static bool TryParseResetArgs(string[] args, out string? username, out bool fromStdin)
{
    username = null;
    fromStdin = false;
    if (args.Length == 0 || args[0] != "--reset-password")
    {
        return false;
    }

    for (var i = 1; i < args.Length; i++)
    {
        if (args[i] == "--password-stdin")
        {
            fromStdin = true;
        }
        else if (!args[i].StartsWith('-'))
        {
            username = args[i];
        }
        else
        {
            return false;
        }
    }

    return true;
}

static int ResetPassword(string? username, bool fromStdin)
{
    try
    {
        if (fromStdin)
        {
            return ResetPasswordFromStdin(username ?? "");
        }

        if (OperatingSystem.IsWindows() && !IsProcessAdmin())
        {
            try
            {
                RelaunchElevatedReset(username);
                return 0;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                PixelPipe.Web.PasswordDialog.Tell(
                    "Windows permission was cancelled. The password was not changed.",
                    false);
                return 1;
            }
        }

        var collected = PixelPipe.Web.PasswordDialog.TryCollect(username, out var user, out var password);
        if (collected is null)
        {
            return ResetPasswordFromConsole(username);
        }

        if (collected == false)
        {
            return 1;
        }

        var outcome = ApplyPasswordReset(user, password, password);
        LogReset(outcome.Message);
        PixelPipe.Web.PasswordDialog.Tell(outcome.Message, outcome.Code == 0);
        return outcome.Code;
    }
    catch (Exception ex)
    {
        var message = FriendlyResetError(ex);
        LogReset(message);
        PixelPipe.Web.PasswordDialog.Tell(message, false);
        return 1;
    }
}

static int ResetPasswordFromStdin(string username)
{
    if (OperatingSystem.IsWindows() && !IsProcessAdmin())
    {
        Console.Error.WriteLine("Run --password-stdin from an elevated process. It does not show a dialog.");
        return 1;
    }

    var password = Console.In.ReadLine() ?? "";
    var confirm = Console.In.ReadLine() ?? "";
    var outcome = ApplyPasswordReset(username, password, confirm);
    LogReset(outcome.Message);
    if (outcome.Code == 0)
    {
        Console.WriteLine(outcome.Message);
    }
    else
    {
        Console.Error.WriteLine(outcome.Message);
    }

    return outcome.Code;
}

static int ResetPasswordFromConsole(string? username)
{
    ResetConsole.Bind();
    var user = username ?? "";
    if (string.IsNullOrWhiteSpace(user))
    {
        Console.Write("Username: ");
        user = Console.ReadLine() ?? "";
    }

    Console.Write("New password: ");
    var password = Console.ReadLine() ?? "";
    Console.Write("Confirm password: ");
    var confirm = Console.ReadLine() ?? "";
    var outcome = ApplyPasswordReset(user.Trim(), password, confirm);
    LogReset(outcome.Message);
    if (outcome.Code == 0)
    {
        Console.WriteLine(outcome.Message);
    }
    else
    {
        Console.Error.WriteLine(outcome.Message);
    }

    return outcome.Code;
}

static (int Code, string Message) ApplyPasswordReset(string username, string password, string confirm)
{
    var problem = PasswordRules.Check(password, confirm);
    if (problem is not null)
    {
        return (1, problem);
    }

    AppPaths.EnsureDataDirectory();
    var accounts = new AccountStore();
    var user = accounts.FindByName(username);
    if (user is null)
    {
        return (1, $"No account named {username}.");
    }

    accounts.SetPassword(user.Id, password);
    accounts.ClearLockout(user.Id);
    return (0, $"Password updated for {user.Username}.");
}

static string FriendlyResetError(Exception ex)
{
    if (ex is Microsoft.Data.Sqlite.SqliteException sqlite && sqlite.SqliteErrorCode == 8)
    {
        return "The accounts database is read-only. Reinstall PixelPipe so Users can modify %ProgramData%\\PixelPipe, then try the reset again.";
    }

    return "Could not reset the password: " + ex.Message;
}

static bool IsProcessAdmin()
{
    if (!OperatingSystem.IsWindows())
    {
        return true;
    }

    using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
    return new System.Security.Principal.WindowsPrincipal(identity)
        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
}

static void RelaunchElevatedReset(string? username)
{
    var arguments = "--reset-password";
    if (!string.IsNullOrWhiteSpace(username))
    {
        arguments += " \"" + username.Replace("\"", "", StringComparison.Ordinal) + "\"";
    }

    Process.Start(new ProcessStartInfo
    {
        FileName = Environment.ProcessPath ?? "",
        Arguments = arguments,
        UseShellExecute = true,
        Verb = "runas",
    });
}

static void LogReset(string message)
{
    try
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "PixelPipe");
        Directory.CreateDirectory(dir);
        File.AppendAllText(
            Path.Combine(dir, "pixelpipe-web.log"),
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} reset: {message}{Environment.NewLine}");
    }
    catch
    {
        // The reset still reports the message in the dialog or on stderr.
    }
}

static async Task SignInAsync(HttpContext http, AccountUser user, bool remember)
{
    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, user.Id),
        new(ClaimTypes.Name, user.Username),
        new(ClaimTypes.Role, user.Role),
    };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    var properties = new AuthenticationProperties
    {
        IsPersistent = remember,
        ExpiresUtc = remember ? DateTimeOffset.UtcNow.AddDays(30) : null,
    };
    await http.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity),
        properties);
}

internal sealed record SessionStartBody(string Rom, string? Slot);

public partial class Program
{
}

static class ResetConsole
{
    public static void Bind()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            if (!AttachConsole(-1))
            {
                AllocConsole();
            }

            var input = new FileStream("CONIN$", FileMode.Open, FileAccess.Read);
            var output = new FileStream("CONOUT$", FileMode.Open, FileAccess.Write);
            var error = new FileStream("CONOUT$", FileMode.Open, FileAccess.Write);
            Console.SetIn(new StreamReader(input));
            Console.SetOut(new StreamWriter(output) { AutoFlush = true });
            Console.SetError(new StreamWriter(error) { AutoFlush = true });
        }
        catch
        {
            // A GUI build with no console still exits with a code.
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();
}

internal sealed record RomPathBody(string Path);
internal sealed record UpdateSettingsBody(bool IncludePreviews, string? Token, bool ClearToken);
internal sealed record UpdateApplyBody(bool Confirm);
