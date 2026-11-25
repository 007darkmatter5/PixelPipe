using System.Diagnostics;
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
                if (!string.IsNullOrWhiteSpace(data) && !IsUnderProgramFiles(data))
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

    public static bool IsUnderProgramFiles(string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        })
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static void EnsureDataDirectory()
    {
        var dir = DataDirectory();
        if (IsUnderProgramFiles(dir))
        {
            throw new InvalidOperationException("PixelPipe data cannot be stored in Program Files. Use %ProgramData%\\PixelPipe.");
        }

        Directory.CreateDirectory(dir);
        TryGrantUsersModify(dir);
    }

    public static void TryGrantUsersModify(string directory)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "icacls.exe",
                Arguments = $"\"{directory}\" /grant *S-1-5-32-545:(OI)(CI)M /T /C",
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(start);
            process?.WaitForExit(10000);
        }
        catch
        {
            // A standard user cannot change ACLs. The installer grants them while elevated.
        }
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
