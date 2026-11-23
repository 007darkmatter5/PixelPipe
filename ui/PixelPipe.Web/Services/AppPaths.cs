using System.Text.Json.Nodes;

namespace PixelPipe.Web.Services;

public static class AppPaths
{
    public static string DataDirectory()
    {
        var fromEnv = Environment.GetEnvironmentVariable("PIXELPIPE_DATA");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        var program = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "PixelPipe");
        var config = Path.Combine(program, "config.json");
        if (File.Exists(config))
        {
            try
            {
                var data = JsonNode.Parse(File.ReadAllText(config))?["data"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(data))
                {
                    return data;
                }
            }
            catch
            {
                // Fall through to the program data folder.
            }
        }

        return program;
    }

    public static string AuthSecretPath() => Path.Combine(DataDirectory(), "auth.secret");

    public static string EnsureAuthSecret()
    {
        var path = AuthSecretPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            var bytes = new byte[32];
            System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
            File.WriteAllText(path, Convert.ToHexString(bytes).ToLowerInvariant());
        }

        return File.ReadAllText(path).Trim();
    }
}
