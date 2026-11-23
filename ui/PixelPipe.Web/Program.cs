using System.Net.Http.Headers;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using PixelPipe.Web.Services;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Transforms;

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
    var site = SiteSettings.Load();
    if (site.BehindProxy)
    {
        if (context.Request.Headers.TryGetValue("X-Forwarded-For", out var forwardedFor))
        {
            var first = forwardedFor.ToString().Split(',')[0].Trim();
            if (System.Net.IPAddress.TryParse(first, out var address))
            {
                context.Connection.RemoteIpAddress = address;
            }
        }

        if (string.IsNullOrWhiteSpace(site.PublicUrl))
        {
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
                context.Request.Host = new HostString(host.ToString().Split(',')[0].Trim());
            }
        }
    }

    if (site.BehindProxy && Uri.TryCreate(site.PublicUrl, UriKind.Absolute, out var published))
    {
        context.Request.Scheme = published.Scheme;
        context.Request.Host = published.IsDefaultPort
            ? new HostString(published.Host)
            : new HostString(published.Authority);
    }

    await next();
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
    await antiforgery.ValidateRequestAsync(http);
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

    await antiforgery.ValidateRequestAsync(http);
    var form = await http.Request.ReadFormAsync();
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
app.MapGet("/api/app/update", (UpdateService updates) => Results.Json(updates.Snapshot)).RequireAuthorization();
app.MapPost("/api/app/update/check", async (UpdateService updates, CancellationToken cancellationToken) =>
{
    await updates.CheckAsync(cancellationToken);
    return Results.Json(updates.Snapshot);
}).RequireAuthorization();
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
}).RequireAuthorization();

app.MapRazorComponents<PixelPipe.Web.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();

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

internal sealed record RomPathBody(string Path);
internal sealed record UpdateSettingsBody(bool IncludePreviews, string? Token, bool ClearToken);
internal sealed record UpdateApplyBody(bool Confirm);
