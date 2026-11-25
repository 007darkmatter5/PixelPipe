using Microsoft.Data.Sqlite;
using PixelPipe.Web.Services;
using Xunit;

namespace PixelPipe.Web.Tests;

public class DatabaseAccessTests
{
    [Fact]
    public void Accounts_database_uses_wal_and_a_busy_timeout()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pixelpipe-wal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new AccountStore(Path.Combine(dir, "accounts.db"));
        Assert.Equal("wal", store.JournalMode());
        Assert.Equal(5000, store.BusyTimeout());

        using var other = new SqliteConnection($"Data Source={Path.Combine(dir, "accounts.db")}");
        other.Open();
        using var cmd = other.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", Convert.ToString(cmd.ExecuteScalar()));
    }

    [Fact]
    public void Accounts_database_is_not_placed_in_program_files()
    {
        var data = Path.Combine(Path.GetTempPath(), "ProgramData", "PixelPipe", "accounts.db");
        Assert.False(AppPaths.IsUnderProgramFiles(data));
        var program = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(program))
        {
            return;
        }

        Assert.True(AppPaths.IsUnderProgramFiles(Path.Combine(program, "PixelPipe", "accounts.db")));
        Assert.Throws<InvalidOperationException>(() => new AccountStore(Path.Combine(program, "PixelPipe", "accounts.db")));
    }

    [Fact]
    public void A_second_connection_can_write_while_the_first_is_open()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pixelpipe-busy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "accounts.db");
        var first = new AccountStore(path);
        first.Create("ada", "correct-horse", "Admin");
        var second = new AccountStore(path);
        var user = second.FindByName("ada");
        Assert.NotNull(user);
        second.SetPassword(user!.Id, "another-horse");
        Assert.True(first.CheckPassword("ada", "another-horse").Ok);
    }
}
