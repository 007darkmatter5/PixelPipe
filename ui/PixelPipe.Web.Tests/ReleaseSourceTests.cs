using PixelPipe.UpdateCore;
using Xunit;

namespace PixelPipe.Web.Tests;

public sealed class ReleaseSourceTests
{
    [Fact]
    public void Only_this_repos_release_downloads_are_accepted()
    {
        const string url = "https://github.com/007darkmatter5/PixelPipe/releases/download/v0.3.5-preview/PixelPipe-Setup-0.3.5.exe";
        Assert.True(ReleaseSource.TryGetChecksumUrl(url, out var sha));
        Assert.Equal(url + ".sha256", sha);
        Assert.False(ReleaseSource.TryGetChecksumUrl("https://example.com/PixelPipe-Setup-0.3.5.exe", out _));
        Assert.False(ReleaseSource.TryGetChecksumUrl("http://github.com/007darkmatter5/PixelPipe/releases/download/v0.3.5-preview/PixelPipe-Setup-0.3.5.exe", out _));
        Assert.False(ReleaseSource.TryGetChecksumUrl("https://github.com/other/PixelPipe/releases/download/v0.3.5-preview/PixelPipe-Setup-0.3.5.exe", out _));
        Assert.False(ReleaseSource.TryGetChecksumUrl(url + "?evil=1", out _));
    }

    [Fact]
    public void Checksums_compare_the_first_token_only()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, "pixelpipe"u8.ToArray());
            var hash = InstallerHash.Compute(path);
            Assert.Equal(64, hash.Length);
            Assert.True(InstallerHash.EqualsHash(hash, hash + "  PixelPipe-Setup-0.3.5.exe\n"));
            Assert.False(InstallerHash.EqualsHash(hash, new string('a', 64)));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
