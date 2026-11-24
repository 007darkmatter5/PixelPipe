using PixelPipe.Web.Services;
using Xunit;

namespace PixelPipe.Web.Tests;

public class AccountTests
{
    [Fact]
    public void Password_round_trip_and_lockout()
    {
        var store = NewStore();
        store.Create("ada", "correct-horse", "Admin");
        Assert.True(store.CheckPassword("ada", "correct-horse").Ok);
        for (var i = 0; i < 5; i++)
        {
            Assert.False(store.CheckPassword("ada", "wrong-password").Ok);
        }

        var locked = store.CheckPassword("ada", "correct-horse");
        Assert.False(locked.Ok);
        Assert.Contains("locked", locked.Error, StringComparison.OrdinalIgnoreCase);
        store.ClearLockout(store.FindByName("ada")!.Id);
        Assert.True(store.CheckPassword("ada", "correct-horse").Ok);
    }

    [Fact]
    public void Two_users_do_not_share_progress_or_save_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "pixelpipe-saves-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var ada = SaveLayout.ProgressDirectory(root, "user-ada", "nes", "abc123");
        var bob = SaveLayout.ProgressDirectory(root, "user-bob", "nes", "abc123");
        Directory.CreateDirectory(ada);
        Directory.CreateDirectory(bob);
        File.WriteAllText(Path.Combine(ada, "sram.bin"), "ada-sram");
        Assert.False(File.Exists(Path.Combine(bob, "sram.bin")));
        Assert.NotEqual(ada, bob);

        var store = NewStore();
        var admin = store.Create("ada", "correct-horse", "Admin");
        var user = store.Create("bob", "correct-horse", "User");
        store.TouchProgress(admin.Id, "abc123", "nes", "Ada game", "ada.nes", "auto");
        store.TouchProgress(user.Id, "abc123", "nes", "Bob game", "bob.nes", "1");
        var adaRows = store.ProgressFor(admin.Id);
        var bobRows = store.ProgressFor(user.Id);
        Assert.Single(adaRows);
        Assert.Equal("Ada game", adaRows[0].Title);
        Assert.Equal("Bob game", bobRows[0].Title);
    }

    [Fact]
    public void Legacy_saves_move_to_the_first_admin()
    {
        var root = Path.Combine(Path.GetTempPath(), "pixelpipe-migrate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "saves"));
        File.WriteAllText(Path.Combine(root, "saves", "aabbccdd.srm"), "sram");
        File.WriteAllText(Path.Combine(root, "saves", "aabbccdd.state"), "state");
        var moved = SaveLayout.MigrateLegacy(root, "admin-id", new[]
        {
            ("aabbccdd", (string?)null, "snes", "hashhash"),
        });
        Assert.Equal(2, moved);
        Assert.Equal("sram", File.ReadAllText(Path.Combine(root, "saves", "admin-id", "snes", "hashhash", "sram.bin")));
        Assert.False(File.Exists(Path.Combine(root, "saves", "aabbccdd.srm")));
    }

    private static AccountStore NewStore()
    {
        var path = Path.Combine(Path.GetTempPath(), "pixelpipe-accounts-" + Guid.NewGuid().ToString("N") + ".db");
        return new AccountStore(path);
    }
}
