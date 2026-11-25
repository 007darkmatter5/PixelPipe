namespace PixelPipe.UpdateCore;

public static class UpdaterLog
{
    public static string DataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PixelPipe");

    public static void Write(string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} updater: {message}{Environment.NewLine}";
        try
        {
            var dir = DataDirectory();
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "pixelpipe.log"), line);
            File.AppendAllText(Path.Combine(dir, "updater.log"), line);
        }
        catch
        {
            // The update still has to proceed when the log folder is locked.
        }
    }

    public static void WriteStatus(string phase, string message, string? version, string? error)
    {
        try
        {
            var dir = DataDirectory();
            Directory.CreateDirectory(dir);
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                phase,
                message,
                version,
                error,
                updatedUtc = DateTimeOffset.UtcNow.ToString("O"),
            });
            File.WriteAllText(Path.Combine(dir, "updater-status.json"), json);
        }
        catch
        {
            // The page falls back to the log if the status file cannot be written.
        }

        Write(phase + ": " + message + (string.IsNullOrWhiteSpace(error) ? "" : " (" + error + ")"));
    }
}
