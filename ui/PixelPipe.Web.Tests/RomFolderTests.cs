using PixelPipe.Web.Services;
using Xunit;

namespace PixelPipe.Web.Tests;

public class RomFolderTests
{
    [Fact]
    public void A_missing_drive_letter_path_is_kept()
    {
        var listing = RomFolder.List(@"M:\Games\ROMs");
        Assert.Equal(@"M:\Games\ROMs", listing.Path);
        Assert.Empty(listing.Entries);
    }
}
