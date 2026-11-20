using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using PixelPipe.Web.Services;

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
builder.Services.AddMudServices();
builder.Services.AddSingleton<LibraryService>();
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

app.UseStaticFiles();
app.UseAntiforgery();

app.MapGet("/media/boxart/{*path}", (string path, LibraryService library) =>
{
    var relative = Uri.UnescapeDataString(path);
    var game = library.Find(relative);
    if (game?.BoxArtFile is { } file && File.Exists(file))
    {
        return Results.File(file, "image/png");
    }

    return Results.Content(library.PlaceholderSvg(game, relative), "image/svg+xml");
});

app.MapGet("/api/library/folders", (string? path) => Results.Json(RomFolder.List(path)));
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
});
app.MapGet("/api/app/update", (UpdateService updates) => Results.Json(updates.Snapshot));
app.MapPost("/api/app/update/check", async (UpdateService updates, CancellationToken cancellationToken) =>
{
    await updates.CheckAsync(cancellationToken);
    return Results.Json(updates.Snapshot);
});
app.MapPost("/api/app/update/settings", async (UpdateSettingsBody body, UpdateService updates, CancellationToken cancellationToken) =>
{
    updates.SaveSettings(body.IncludePreviews, body.Token, body.ClearToken);
    await updates.CheckAsync(cancellationToken);
    return Results.Json(updates.Snapshot);
});
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
});

app.MapRazorComponents<PixelPipe.Web.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();

internal sealed record RomPathBody(string Path);
internal sealed record UpdateSettingsBody(bool IncludePreviews, string? Token, bool ClearToken);
internal sealed record UpdateApplyBody(bool Confirm);
