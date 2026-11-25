using PixelPipe.Web.Services;
using Xunit;

namespace PixelPipe.Web.Tests;

public sealed class UpdateTests
{
    [Fact]
    public void Preview_builds_sort_after_the_same_release_numbers()
    {
        Assert.True(AppVersion.TryParse("v0.2.2-preview", out var preview));
        Assert.True(AppVersion.TryParse("0.2.1", out var installed));
        Assert.True(AppVersion.TryParse("v0.2.1-preview", out var samePreview));
        Assert.True(preview.CompareTo(installed) > 0);
        Assert.True(installed.CompareTo(samePreview) > 0);
        Assert.Equal(0, installed.CompareTo(new AppVersion(0, 2, 1, null)));
    }

    [Fact]
    public void Preview_releases_are_optional_and_need_both_assets()
    {
        var current = new AppVersion(0, 2, 1, null);
        var releases = new[]
        {
            new RemoteRelease("v0.2.2-preview", new AppVersion(0, 2, 2, "preview"), true, "notes", "https://example", 10, 11),
            new RemoteRelease("v0.2.0", new AppVersion(0, 2, 0, null), false, null, null, 1, 2),
            new RemoteRelease("v0.9.0-preview", new AppVersion(0, 9, 0, "preview"), true, null, null, null, null),
        };
        Assert.Equal("v0.2.2-preview", ReleaseSelection.NewerThan(releases, current, includePreviews: true)?.Tag);
        Assert.Null(ReleaseSelection.NewerThan(releases, current, includePreviews: false));
    }

    [Fact]
    public void Checksum_file_accepts_the_hash_and_rejects_a_mismatch()
    {
        var data = "pixelpipe"u8.ToArray();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();
        Assert.True(ChecksumFile.Matches(data, $"{hash}  PixelPipe-Setup.exe\n"));
        Assert.False(ChecksumFile.Matches(data, new string('a', 64)));
        Assert.False(ChecksumFile.Matches(data, "not-a-hash"));
    }

    [Fact]
    public void Release_json_keeps_the_setup_asset_ids()
    {
        const string json = """
            [
              {"tag_name":"v0.2.2-preview","draft":false,"prerelease":true,"body":"Fixes","html_url":"https://example/r","assets":[
                {"id":5,"name":"PixelPipe-Setup-0.2.3.exe"},
                {"id":6,"name":"PixelPipe-Setup-0.2.3.exe.sha256"},
                {"id":7,"name":"PixelPipe-Setup.exe"},
                {"id":8,"name":"PixelPipe-Setup.exe.sha256"}
              ]}
            ]
            """;
        var parsed = UpdateService.ParseReleases(json);
        var chosen = ReleaseSelection.NewerThan(parsed, new AppVersion(0, 2, 1, null), includePreviews: true);
        Assert.Equal(5, chosen?.ExeAssetId);
        Assert.Equal(6, chosen?.ShaAssetId);
        Assert.Equal("PixelPipe-Setup-0.2.3.exe", chosen?.ExeName);
        Assert.Equal("Fixes", chosen?.Notes);
    }

    [Fact]
    public async Task A_032_version_finds_033_or_newer_on_the_public_release_list()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var releases = await GitHubReleases.LoadAsync(client, token: null, "007darkmatter5/PixelPipe", CancellationToken.None);
        var found = ReleaseSelection.NewerThan(releases, new AppVersion(0, 3, 2, null), includePreviews: true);
        Assert.NotNull(found);
        Assert.True(found.Version.CompareTo(new AppVersion(0, 3, 3, "preview")) >= 0, found.Tag);
        Assert.NotNull(found.ExeAssetId);
        Assert.NotNull(found.ShaAssetId);
    }

    [Fact]
    public void Old_unversioned_installer_name_still_matches()
    {
        const string json = """
            [
              {"tag_name":"v0.2.2-preview","draft":false,"prerelease":true,"body":"old","assets":[
                {"id":1,"name":"PixelPipe-Setup.exe"},
                {"id":2,"name":"PixelPipe-Setup.exe.sha256"}
              ]}
            ]
            """;
        var chosen = ReleaseSelection.NewerThan(
            UpdateService.ParseReleases(json),
            new AppVersion(0, 2, 1, null),
            includePreviews: true);
        Assert.Equal(1, chosen?.ExeAssetId);
        Assert.Equal(2, chosen?.ShaAssetId);
    }
}
