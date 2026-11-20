using System.IO.Compression;
using PixelPipe.Web.Services;

namespace PixelPipe.Web.Tests;

public sealed class RomScannerTests
{
    [Fact]
    public void Nested_folder_names_and_zip_pick_the_largest_rom()
    {
        var root = Path.Combine(Path.GetTempPath(), "pixelpipe-csharp-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "consoles", "SNES", "usa"));
        Directory.CreateDirectory(Path.Combine(root, "Mega Drive"));
        File.WriteAllBytes(Path.Combine(root, "consoles", "SNES", "usa", "demo.bin"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(root, "Mega Drive", "sonic.bin"), [4, 5]);
        var zipPath = Path.Combine(root, "packs", "favorites.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var note = zip.CreateEntry("readme.txt");
            using (var writer = new StreamWriter(note.Open()))
            {
                writer.Write("notes");
            }

            var small = zip.CreateEntry("tiny.sfc");
            using (var stream = small.Open())
            {
                stream.Write(new byte[] { 1, 2, 3, 4 });
            }

            var large = zip.CreateEntry("bigger.nes");
            using (var stream = large.Open())
            {
                stream.Write(new byte[32]);
            }
        }

        var found = RomScanner.Scan(root);
        Assert.Equal("snes", found.Single(game => game.RelativePath.EndsWith("demo.bin")).SystemId);
        Assert.Equal("genesis", found.Single(game => game.RelativePath.EndsWith("sonic.bin")).SystemId);
        var zipped = found.Single(game => game.RelativePath.Contains("favorites.zip"));
        Assert.Equal("nes", zipped.SystemId);
        Assert.EndsWith("#bigger.nes", zipped.RelativePath);

        var bundled = RomScanner.Scan(root, "bundled");
        Assert.Contains(bundled, game => game.RelativePath.StartsWith("bundled/consoles/SNES/"));
        Directory.Delete(root, recursive: true);
    }

    [Theory]
    [InlineData("NES", "nes")]
    [InlineData("Mega Drive", "genesis")]
    [InlineData("SNES", "snes")]
    [InlineData("notes", null)]
    public void Folder_names_map_to_systems(string name, string? expected)
    {
        Assert.Equal(expected, RomScanner.SystemFromFolderName(name));
    }
}
