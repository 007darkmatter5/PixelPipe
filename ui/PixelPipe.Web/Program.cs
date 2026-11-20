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

app.MapRazorComponents<PixelPipe.Web.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();
