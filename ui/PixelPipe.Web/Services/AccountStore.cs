using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;

namespace PixelPipe.Web.Services;

public sealed record AccountUser(
    string Id,
    string Username,
    string PasswordHash,
    string Role,
    bool Disabled,
    int FailedLogins,
    DateTimeOffset? LockoutUntil);

public sealed record ProgressRow(
    string UserId,
    string GameHash,
    string System,
    string Title,
    string RelativePath,
    DateTimeOffset LastPlayed,
    string LastSlot);

public sealed class AccountStore
{
    private readonly string _path;
    private readonly PasswordHasher<string> _hasher = new();
    private readonly object _gate = new();

    public AccountStore()
        : this(Path.Combine(AppPaths.DataDirectory(), "accounts.db"))
    {
    }

    public AccountStore(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        lock (_gate)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS users (
                  id TEXT PRIMARY KEY,
                  username TEXT NOT NULL UNIQUE COLLATE NOCASE,
                  password_hash TEXT NOT NULL,
                  role TEXT NOT NULL,
                  disabled INTEGER NOT NULL DEFAULT 0,
                  failed_logins INTEGER NOT NULL DEFAULT 0,
                  lockout_until TEXT NULL,
                  created_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS progress (
                  user_id TEXT NOT NULL,
                  game_hash TEXT NOT NULL,
                  system TEXT NOT NULL,
                  title TEXT NOT NULL,
                  relative_path TEXT NOT NULL,
                  last_played_utc TEXT NOT NULL,
                  last_slot TEXT NOT NULL,
                  PRIMARY KEY (user_id, game_hash)
                );
                """;
            cmd.ExecuteNonQuery();
        }
    }

    public bool Any()
    {
        lock (_gate)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM users";
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }
    }

    public IReadOnlyList<AccountUser> List()
    {
        lock (_gate)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT id, username, password_hash, role, disabled, failed_logins, lockout_until FROM users ORDER BY username";
            return ReadUsers(cmd);
        }
    }

    public AccountUser? FindByName(string username)
    {
        lock (_gate)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT id, username, password_hash, role, disabled, failed_logins, lockout_until FROM users WHERE username = $name";
            cmd.Parameters.AddWithValue("$name", username.Trim());
            return ReadUsers(cmd).FirstOrDefault();
        }
    }

    public AccountUser? FindById(string id)
    {
        lock (_gate)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT id, username, password_hash, role, disabled, failed_logins, lockout_until FROM users WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            return ReadUsers(cmd).FirstOrDefault();
        }
    }

    public AccountUser Create(string username, string password, string role)
    {
        if (password.Length < 8)
        {
            throw new InvalidOperationException("Password must be at least 8 characters.");
        }

        var id = Guid.NewGuid().ToString("D");
        var hash = _hasher.HashPassword(username, password);
        lock (_gate)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO users (id, username, password_hash, role, created_utc)
                VALUES ($id, $name, $hash, $role, $created)
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$name", username.Trim());
            cmd.Parameters.AddWithValue("$hash", hash);
            cmd.Parameters.AddWithValue("$role", role == "Admin" ? "Admin" : "User");
            cmd.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        return FindById(id)!;
    }

    public PasswordCheck CheckPassword(string username, string password)
    {
        var user = FindByName(username);
        if (user is null)
        {
            return new PasswordCheck(false, null, "That username or password is incorrect.");
        }

        if (user.Disabled)
        {
            return new PasswordCheck(false, user, "This account is disabled.");
        }

        if (user.LockoutUntil is { } until && until > DateTimeOffset.UtcNow)
        {
            return new PasswordCheck(false, user, "This account is locked. An admin can unlock it, or on this PC use Reset admin password in the tray.");
        }

        var verified = _hasher.VerifyHashedPassword(user.Username, user.PasswordHash, password);
        if (verified == PasswordVerificationResult.Failed)
        {
            RegisterFailure(user.Id);
            return new PasswordCheck(false, user, "That username or password is incorrect.");
        }

        ClearFailures(user.Id);
        return new PasswordCheck(true, user, null);
    }

    public void SetPassword(string id, string password)
    {
        if (password.Length < 8)
        {
            throw new InvalidOperationException("Password must be at least 8 characters.");
        }

        var user = FindById(id) ?? throw new InvalidOperationException("User not found.");
        var hash = _hasher.HashPassword(user.Username, password);
        lock (_gate)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE users SET password_hash = $hash, failed_logins = 0, lockout_until = NULL WHERE id = $id";
            cmd.Parameters.AddWithValue("$hash", hash);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void ClearLockout(string id)
    {
        lock (_gate)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE users SET failed_logins = 0, lockout_until = NULL WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void SetDisabled(string id, bool disabled)
    {
        lock (_gate)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE users SET disabled = $disabled WHERE id = $id";
            cmd.Parameters.AddWithValue("$disabled", disabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void Delete(string id)
    {
        lock (_gate)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM users WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void TouchProgress(string userId, string gameHash, string system, string title, string relativePath, string slot)
    {
        lock (_gate)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO progress (user_id, game_hash, system, title, relative_path, last_played_utc, last_slot)
                VALUES ($user, $hash, $system, $title, $path, $played, $slot)
                ON CONFLICT(user_id, game_hash) DO UPDATE SET
                  title = excluded.title,
                  relative_path = excluded.relative_path,
                  last_played_utc = excluded.last_played_utc,
                  last_slot = excluded.last_slot
                """;
            cmd.Parameters.AddWithValue("$user", userId);
            cmd.Parameters.AddWithValue("$hash", gameHash);
            cmd.Parameters.AddWithValue("$system", system);
            cmd.Parameters.AddWithValue("$title", title);
            cmd.Parameters.AddWithValue("$path", relativePath);
            cmd.Parameters.AddWithValue("$played", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$slot", slot);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<ProgressRow> ProgressFor(string userId)
    {
        lock (_gate)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT user_id, game_hash, system, title, relative_path, last_played_utc, last_slot FROM progress WHERE user_id = $user";
            cmd.Parameters.AddWithValue("$user", userId);
            using var reader = cmd.ExecuteReader();
            var list = new List<ProgressRow>();
            while (reader.Read())
            {
                list.Add(new ProgressRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    DateTimeOffset.Parse(reader.GetString(5)),
                    reader.GetString(6)));
            }

            return list;
        }
    }

    private void RegisterFailure(string id)
    {
        lock (_gate)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                UPDATE users
                SET failed_logins = failed_logins + 1,
                    lockout_until = CASE WHEN failed_logins + 1 >= 5 THEN $until ELSE lockout_until END
                WHERE id = $id
                """;
            cmd.Parameters.AddWithValue("$until", DateTimeOffset.UtcNow.AddMinutes(15).ToString("O"));
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    private void ClearFailures(string id)
    {
        lock (_gate)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE users SET failed_logins = 0, lockout_until = NULL WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection($"Data Source={_path}");
        db.Open();
        return db;
    }

    private static List<AccountUser> ReadUsers(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        var list = new List<AccountUser>();
        while (reader.Read())
        {
            DateTimeOffset? lockout = reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6));
            list.Add(new AccountUser(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4) != 0,
                reader.GetInt32(5),
                lockout));
        }

        return list;
    }
}

public sealed record PasswordCheck(bool Ok, AccountUser? User, string? Error);
