using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace XSeller.Services;

public enum UserRole { Admin, Seller }

public sealed class UserAccount
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public UserRole Role { get; set; }
    public bool Disabled { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string RoleLabel => Role == UserRole.Admin ? "Admin (owner)" : "Seller (employee)";
    public string StatusLabel => Disabled ? "Disabled" : "Active";
}

/// <summary>The signed-in user. When accounts are off, everything is attributed to "Default".</summary>
public static class Session
{
    public const string DefaultUserId = "default";
    public const string DefaultUserName = "Default";

    public static UserAccount? Current { get; private set; }
    public static string UserId => Current?.Id ?? DefaultUserId;
    public static string UserName => Current?.Name ?? DefaultUserName;
    public static bool IsAdmin => Current == null ? !Accounts.Instance.Enabled : Current.Role == UserRole.Admin;
    public static string ShopId => Accounts.Instance.ShopId;
    public static event Action? Changed;

    public static void SignIn(UserAccount u) { Current = u; Accounts.Instance.Audit(u.Id, "login"); Changed?.Invoke(); }
    public static void SignOut() { if (Current != null) Accounts.Instance.Audit(Current.Id, "logout"); Current = null; Changed?.Invoke(); }
}

/// <summary>
/// Optional local accounts in the existing ledger DB (%DOCUMENTS%\XSeller\ledger.db).
/// PIN/password hashes: PBKDF2-HMAC-SHA256, 16-byte random salt, 210k iterations.
/// Also holds key/value shop settings (shop id, prices, split).
/// </summary>
public sealed class Accounts
{
    public const int Iterations = 210_000;
    private static Accounts? _instance;
    public static Accounts Instance => _instance ?? throw new InvalidOperationException("Accounts not initialised");

    private readonly string _conn;

    public static void Init(LedgerDatabase ledger) => _instance = new Accounts(ledger.ConnectionString);

    private Accounts(string conn)
    {
        _conn = conn;
        Exec("""
            CREATE TABLE IF NOT EXISTS users (
                Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Role INTEGER NOT NULL,
                Salt BLOB NOT NULL, Hash BLOB NOT NULL, Iterations INTEGER NOT NULL,
                Disabled INTEGER NOT NULL DEFAULT 0, CreatedUtc TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_users_name ON users(Name COLLATE NOCASE);
            CREATE TABLE IF NOT EXISTS settings (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS audit (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId TEXT, Action TEXT, AtUtc TEXT);
            """);
        if (string.IsNullOrEmpty(Get("shop_id"))) Set("shop_id", Guid.NewGuid().ToString("N"));
    }

    private SqliteConnection Open() { var c = new SqliteConnection(_conn); c.Open(); return c; }

    private void Exec(string sql, params (string, object)[] ps)
    {
        using var c = Open();
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v);
        cmd.ExecuteNonQuery();
    }

    // ---------------- settings
    public string? Get(string key)
    {
        using var c = Open();
        var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Value FROM settings WHERE Key=$k;";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void Set(string key, string value) =>
        Exec("INSERT INTO settings(Key,Value) VALUES($k,$v) ON CONFLICT(Key) DO UPDATE SET Value=$v;", ("$k", key), ("$v", value));

    public string ShopId => Get("shop_id") ?? "";
    public bool Enabled => Get("accounts_enabled") == "1" && ListUsers().Any(u => u.Role == UserRole.Admin && !u.Disabled);

    public void Audit(string userId, string action)
    {
        try { Exec("INSERT INTO audit(UserId,Action,AtUtc) VALUES($u,$a,$t);", ("$u", userId), ("$a", action), ("$t", DateTime.UtcNow.ToString("O"))); }
        catch { }
    }

    // ---------------- users
    public List<UserAccount> ListUsers()
    {
        var list = new List<UserAccount>();
        using var c = Open();
        var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, Role, Disabled, CreatedUtc FROM users ORDER BY Role, Name;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new UserAccount
            {
                Id = r.GetString(0), Name = r.GetString(1), Role = (UserRole)r.GetInt32(2),
                Disabled = r.GetInt32(3) == 1, CreatedUtc = DateTime.Parse(r.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind),
            });
        return list;
    }

    public static void ValidateSecret(string secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length < 4)
            throw new ArgumentException("PIN / password must be at least 4 characters.");
    }

    public UserAccount AddUser(string name, UserRole role, string secret)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) throw new ArgumentException("Enter a name.");
        ValidateSecret(secret);
        if (ListUsers().Any(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("A user with that name already exists.");
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Hash(secret, salt, Iterations);
        var u = new UserAccount { Id = Guid.NewGuid().ToString("N"), Name = name, Role = role, CreatedUtc = DateTime.UtcNow };
        Exec("INSERT INTO users(Id,Name,Role,Salt,Hash,Iterations,Disabled,CreatedUtc) VALUES($i,$n,$r,$s,$h,$it,0,$c);",
            ("$i", u.Id), ("$n", u.Name), ("$r", (int)role), ("$s", salt), ("$h", hash), ("$it", Iterations), ("$c", u.CreatedUtc.ToString("O")));
        Audit(Session.UserId, "add-user:" + u.Name);
        return u;
    }

    public void ResetSecret(string userId, string secret)
    {
        ValidateSecret(secret);
        var salt = RandomNumberGenerator.GetBytes(16);
        Exec("UPDATE users SET Salt=$s, Hash=$h, Iterations=$it WHERE Id=$i;",
            ("$s", salt), ("$h", Hash(secret, salt, Iterations)), ("$it", Iterations), ("$i", userId));
        Audit(Session.UserId, "reset:" + userId);
    }

    public void SetDisabled(string userId, bool disabled)
    {
        var users = ListUsers();
        var target = users.First(u => u.Id == userId);
        if (disabled && target.Role == UserRole.Admin && users.Count(u => u.Role == UserRole.Admin && !u.Disabled) <= 1)
            throw new InvalidOperationException("You can't disable the last active admin.");
        Exec("UPDATE users SET Disabled=$d WHERE Id=$i;", ("$d", disabled ? 1 : 0), ("$i", userId));
        Audit(Session.UserId, (disabled ? "disable:" : "enable:") + userId);
    }

    /// <summary>Constant-time verify. Returns the user on success.</summary>
    public UserAccount? Verify(string userId, string secret)
    {
        using var c = Open();
        var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Salt, Hash, Iterations, Disabled FROM users WHERE Id=$i;";
        cmd.Parameters.AddWithValue("$i", userId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var salt = (byte[])r["Salt"];
        var hash = (byte[])r["Hash"];
        var it = r.GetInt32(2);
        var disabled = r.GetInt32(3) == 1;
        var ok = CryptographicOperations.FixedTimeEquals(Hash(secret ?? "", salt, it), hash);
        if (!ok || disabled) { Audit(userId, "login-failed"); return null; }
        return ListUsers().FirstOrDefault(u => u.Id == userId);
    }

    private static byte[] Hash(string secret, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(secret, salt, iterations, HashAlgorithmName.SHA256, 32);

    /// <summary>First run of "Enable accounts": creates the owner admin and turns login on.</summary>
    public UserAccount EnableWithFirstAdmin(string name, string secret)
    {
        var admin = AddUser(name, UserRole.Admin, secret);
        Set("accounts_enabled", "1");
        return admin;
    }

    public void DisableAccounts() => Set("accounts_enabled", "0");
}

/// <summary>Revenue split — the ONE place the percentages live (admin-configurable later).</summary>
public static class RevenueSplit
{
    public const int DefaultPlatformPercent = 50;
    public const int DefaultShopPercent = 40;
    public const int DefaultEmployeePercent = 10;

    public static (int Platform, int Shop, int Employee) Current
    {
        get
        {
            try
            {
                var s = Accounts.Instance.Get("split");
                if (s != null)
                {
                    var p = s.Split('/').Select(int.Parse).ToArray();
                    if (p.Length == 3 && p.All(x => x >= 0) && p.Sum() == 100) return (p[0], p[1], p[2]);
                }
            }
            catch { }
            return (DefaultPlatformPercent, DefaultShopPercent, DefaultEmployeePercent);
        }
    }

    public static (decimal Platform, decimal Shop, decimal Employee) Split(decimal revenue)
    {
        var (p, s, _) = Current;
        var platform = Math.Round(revenue * p / 100m, 2);
        var shop = Math.Round(revenue * s / 100m, 2);
        return (platform, shop, revenue - platform - shop); // employee gets the rounding remainder
    }
}

/// <summary>Prices for plain (unprotected) sends: per category, optional per-item override. Protected = package TokenPrice.</summary>
public static class Prices
{
    public static readonly string[] Categories = ["video", "pdf", "image", "exe", "other", "pass"];

    public static string CategoryOf(Models.LibraryItem it) => it.IsEncrypted ? "protected" : it.Kind switch
    {
        Models.MediaKind.Video => "video",
        Models.MediaKind.Pdf => "pdf",
        Models.MediaKind.Image => "image",
        Models.MediaKind.Exe => "exe",
        _ => "other",
    };

    public static int GetCategory(string cat) =>
        int.TryParse(Accounts.Instance.Get("price:" + cat), out var v) ? v : 0;

    public static void SetCategory(string cat, int price) => Accounts.Instance.Set("price:" + cat, Math.Max(0, price).ToString());

    public static int? GetItem(string path) =>
        int.TryParse(Accounts.Instance.Get("price-item:" + path.ToLowerInvariant()), out var v) ? v : null;

    public static void SetItem(string path, int? price)
    {
        if (price == null) Accounts.Instance.Set("price-item:" + path.ToLowerInvariant(), "");
        else Accounts.Instance.Set("price-item:" + path.ToLowerInvariant(), Math.Max(0, price.Value).ToString());
    }

    public static int PriceOf(Models.LibraryItem it) =>
        it.IsEncrypted ? it.TokenPrice : GetItem(it.FilePath) ?? GetCategory(CategoryOf(it));
}
