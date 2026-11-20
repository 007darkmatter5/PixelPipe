using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PixelPipe.Web.Services;

public static class RomFolder
{
    public sealed record Entry(string Name, string Path);

    public sealed record Listing(string? Path, string? Parent, string? Unc, IReadOnlyList<Entry> Entries);

    public static Listing List(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            var drives = DriveInfo.GetDrives()
                .Select(drive => new Entry(drive.Name, drive.Name))
                .ToList();
            return new Listing(null, null, null, drives);
        }

        var full = path.Trim();
        string? parent = null;
        try
        {
            var info = Directory.GetParent(full);
            parent = info?.FullName;
        }
        catch
        {
            parent = null;
        }

        var entries = new List<Entry>();
        if (Directory.Exists(full))
        {
            try
            {
                entries.AddRange(Directory.EnumerateDirectories(full)
                    .Select(dir => new Entry(System.IO.Path.GetFileName(dir), dir))
                    .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase));
            }
            catch
            {
                // A mapped drive can disappear between the click and the listing.
            }
        }

        return new Listing(full, parent, TryUnc(full), entries);
    }

    public static (string Path, string? Unc) Remember(string path)
    {
        var trimmed = path.Trim();
        var unc = TryUnc(trimmed);
        var file = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "PixelPipe",
            "config.json");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        JsonNode root = File.Exists(file)
            ? JsonNode.Parse(File.ReadAllText(file)) ?? new JsonObject()
            : new JsonObject();
        root["roms"] = trimmed;
        if (string.IsNullOrWhiteSpace(unc))
        {
            root.AsObject().Remove("roms_unc");
        }
        else
        {
            root["roms_unc"] = unc;
        }

        File.WriteAllText(file, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return (trimmed, unc);
    }

    public static string? TryUnc(string path)
    {
        if (!OperatingSystem.IsWindows() || path.Length < 2 || path[1] != ':')
        {
            return null;
        }

        var drive = path[..2];
        var buffer = new StringBuilder(512);
        var length = buffer.Capacity;
        var result = WNetGetConnection(drive, buffer, ref length);
        if (result != 0)
        {
            return null;
        }

        var remote = buffer.ToString().TrimEnd('\\');
        var rest = path.Length > 2 ? path[2..].TrimStart('\\') : "";
        return string.IsNullOrEmpty(rest) ? remote : remote + "\\" + rest;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);
}
