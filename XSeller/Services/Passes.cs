using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using XSeller.Models;

namespace XSeller.Services;

public sealed class PassPhone
{
    public string DeviceId { get; set; } = "";
    public string Model { get; set; } = "";
    public DateTime AddedUtc { get; set; }
    public DateTime? RemovedUtc { get; set; }
    public bool Active => RemovedUtc == null;
}

public sealed class WeeklyPass
{
    public long Id { get; set; }
    public DateTime ActivatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    public int Price { get; set; }
    public int Swaps { get; set; }
    public List<PassPhone> Phones { get; set; } = new();

    public IEnumerable<PassPhone> ActivePhones => Phones.Where(p => p.Active);
    public bool IsActiveAt(DateTime nowUtc) => nowUtc < ExpiresUtc;
    public TimeSpan Left(DateTime nowUtc) => ExpiresUtc > nowUtc ? ExpiresUtc - nowUtc : TimeSpan.Zero;
    /// <summary>Whole days left, rounded up ("1 day left" while &lt;24h remain).</summary>
    public int DaysLeft(DateTime nowUtc) => (int)Math.Ceiling(Left(nowUtc).TotalDays);
}

/// <summary>
/// Weekly pass (offline, this shop PC only). 7 days from activation by the PC clock; the clock used
/// is max(PC clock, latest ledger time, highest time ever seen), so moving the clock back can't extend a pass.
/// Up to 3 phones (by Xama deviceId) per pass; add / remove / swap are logged in the hash-chained ledger.
/// Protected transfers to a covered phone are recorded with price 0 (Kind "pass-use"); plain files are charged as normal.
/// </summary>
public sealed class Passes
{
    public const int MaxPhones = 3;
    public const int MaxSwaps = 3;
    public static readonly TimeSpan Duration = TimeSpan.FromDays(7);
    public static Passes? Instance { get; private set; }

    private readonly LedgerDatabase _ledger;
    private readonly object _gate = new();
    private List<WeeklyPass> _cache = new();
    public event Action? Changed;

    public Passes(LedgerDatabase ledger)
    {
        _ledger = ledger;
        using var c = Open();
        var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS passes (Id INTEGER PRIMARY KEY AUTOINCREMENT, ActivatedUtc TEXT NOT NULL, ExpiresUtc TEXT NOT NULL,
              UserId TEXT NOT NULL, UserName TEXT NOT NULL, Price INTEGER NOT NULL, Swaps INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS pass_phones (PassId INTEGER NOT NULL, DeviceId TEXT NOT NULL, Model TEXT NOT NULL,
              AddedUtc TEXT NOT NULL, RemovedUtc TEXT);
            CREATE INDEX IF NOT EXISTS ix_pass_phones ON pass_phones(DeviceId);
            CREATE TRIGGER IF NOT EXISTS passes_no_delete BEFORE DELETE ON passes BEGIN SELECT RAISE(ABORT, 'passes are append-only'); END;
            CREATE TRIGGER IF NOT EXISTS pass_phones_no_delete BEFORE DELETE ON pass_phones BEGIN SELECT RAISE(ABORT, 'passes are append-only'); END;
            CREATE TRIGGER IF NOT EXISTS passes_no_edit BEFORE UPDATE OF ActivatedUtc, ExpiresUtc, UserId, Price ON passes
              BEGIN SELECT RAISE(ABORT, 'passes are append-only'); END;
            """;
        cmd.ExecuteNonQuery();
        Reload();
        Instance = this;
    }

    private SqliteConnection Open() { var c = new SqliteConnection(_ledger.ConnectionString); c.Open(); return c; }

    /// <summary>Rollback-safe "now": never earlier than the newest ledger time or the highest time seen.</summary>
    public DateTime Now()
    {
        var now = DateTime.UtcNow;
        try
        {
            if (DateTime.TryParse(Accounts.Instance.Get("max_seen_utc"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var m) && m.ToUniversalTime() > now) now = m.ToUniversalTime();
            var last = _ledger.LastSaleTimeUtc();
            if (last > now) now = last;
        }
        catch { }
        return now;
    }

    public bool ClockRolledBack => Now() > DateTime.UtcNow.AddMinutes(10);

    public List<WeeklyPass> All() { lock (_gate) return _cache.ToList(); }

    private void Reload()
    {
        var list = new Dictionary<long, WeeklyPass>();
        using var c = Open();
        var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id, ActivatedUtc, ExpiresUtc, UserId, UserName, Price, Swaps FROM passes ORDER BY Id;";
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                list[r.GetInt64(0)] = new WeeklyPass
                {
                    Id = r.GetInt64(0), ActivatedUtc = T(r.GetString(1)), ExpiresUtc = T(r.GetString(2)),
                    UserId = r.GetString(3), UserName = r.GetString(4), Price = r.GetInt32(5), Swaps = r.GetInt32(6),
                };
        var p = c.CreateCommand();
        p.CommandText = "SELECT PassId, DeviceId, Model, AddedUtc, RemovedUtc FROM pass_phones ORDER BY rowid;";
        using (var r = p.ExecuteReader())
            while (r.Read())
                if (list.TryGetValue(r.GetInt64(0), out var wp))
                    wp.Phones.Add(new PassPhone { DeviceId = r.GetString(1), Model = r.GetString(2), AddedUtc = T(r.GetString(3)), RemovedUtc = r.IsDBNull(4) ? null : T(r.GetString(4)) });
        lock (_gate) _cache = list.Values.ToList();
    }

    private static DateTime T(string s) => DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();

    public static string Norm(string deviceId) => (deviceId ?? "").Trim().ToLowerInvariant();

    /// <summary>Active pass covering this phone right now, else null.</summary>
    public WeeklyPass? ActiveFor(string deviceId)
    {
        var id = Norm(deviceId);
        if (id.Length == 0) return null;
        var now = Now();
        lock (_gate)
            return _cache.LastOrDefault(p => p.IsActiveAt(now) && p.ActivePhones.Any(ph => ph.DeviceId == id));
    }

    /// <summary>Most recent pass this phone was on (active or expired).</summary>
    public WeeklyPass? LatestFor(string deviceId)
    {
        var id = Norm(deviceId);
        lock (_gate) return _cache.LastOrDefault(p => p.Phones.Any(ph => ph.DeviceId == id && ph.Active));
    }

    private void Ledger(string videoId, string title, string deviceId, string model, int price, string kind)
    {
        lock (TransferService.LedgerGate) _ledger.RecordTransfer(new LedgerEntry
        {
            VideoId = videoId, VideoTitle = title, DeviceSerial = deviceId, DeviceModel = model, TokenPrice = price,
            SentAtUtc = DateTime.UtcNow, UserId = Session.UserId, UserName = Session.UserName, ShopId = Session.ShopId,
            Kind = kind, Category = "pass",
        });
    }

    public WeeklyPass Activate(string deviceId, string model)
    {
        var id = Norm(deviceId);
        if (id.Length == 0) throw new InvalidOperationException("Phone not identified — open Xama once on the phone.");
        if (ActiveFor(id) is { } existing) throw new InvalidOperationException($"This phone already has an active pass (#{existing.Id}).");
        var price = Prices.GetCategory("pass");
        var start = Now();
        long passId;
        using (var c = Open())
        {
            using var tx = c.BeginTransaction();
            var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO passes(ActivatedUtc,ExpiresUtc,UserId,UserName,Price) VALUES($a,$e,$u,$n,$p); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$a", start.ToString("O"));
            cmd.Parameters.AddWithValue("$e", (start + Duration).ToString("O"));
            cmd.Parameters.AddWithValue("$u", Session.UserId);
            cmd.Parameters.AddWithValue("$n", Session.UserName);
            cmd.Parameters.AddWithValue("$p", price);
            passId = (long)cmd.ExecuteScalar()!;
            AddPhoneRow(c, passId, id, model);
            tx.Commit();
        }
        Ledger("weekly-pass", "Weekly pass (7 days)", id, $"{model} · pass #{passId}", price, "pass");
        Reload(); Changed?.Invoke(); BackupService.Schedule();
        return All().First(p => p.Id == passId);
    }

    private static void AddPhoneRow(SqliteConnection c, long passId, string deviceId, string model)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO pass_phones(PassId,DeviceId,Model,AddedUtc) VALUES($p,$d,$m,$t);";
        cmd.Parameters.AddWithValue("$p", passId);
        cmd.Parameters.AddWithValue("$d", deviceId);
        cmd.Parameters.AddWithValue("$m", model);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private WeeklyPass Get(long passId) => All().FirstOrDefault(p => p.Id == passId) ?? throw new InvalidOperationException("Pass not found.");

    public void AddPhone(long passId, string deviceId, string model)
    {
        var id = Norm(deviceId);
        var pass = Get(passId);
        if (!pass.IsActiveAt(Now())) throw new InvalidOperationException("This pass has expired.");
        if (pass.ActivePhones.Any(p => p.DeviceId == id)) throw new InvalidOperationException("This phone is already on the pass.");
        if (ActiveFor(id) is { } other) throw new InvalidOperationException($"This phone is on another active pass (#{other.Id}).");
        if (pass.ActivePhones.Count() >= MaxPhones) throw new InvalidOperationException($"A pass covers at most {MaxPhones} phones — remove or swap one first.");
        using (var c = Open()) AddPhoneRow(c, passId, id, model);
        Ledger("weekly-pass-event", $"Pass #{passId}: phone added", id, model, 0, "pass-event");
        Reload(); Changed?.Invoke(); BackupService.Schedule();
    }

    public void RemovePhone(long passId, string deviceId, bool isSwap = false)
    {
        var id = Norm(deviceId);
        var pass = Get(passId);
        if (!pass.ActivePhones.Any(p => p.DeviceId == id)) throw new InvalidOperationException("Phone is not on this pass.");
        if (pass.ActivePhones.Count() <= 1 && !isSwap) throw new InvalidOperationException("A pass needs at least one phone — use Swap instead.");
        var model = pass.ActivePhones.First(p => p.DeviceId == id).Model;
        using (var c = Open())
        {
            var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE pass_phones SET RemovedUtc=$t WHERE PassId=$p AND DeviceId=$d AND RemovedUtc IS NULL;";
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$p", passId);
            cmd.Parameters.AddWithValue("$d", id);
            cmd.ExecuteNonQuery();
        }
        if (!isSwap) Ledger("weekly-pass-event", $"Pass #{passId}: phone removed", id, model, 0, "pass-event");
        else Ledger("weekly-pass-event", $"Pass #{passId}: phone swapped out", id, model, 0, "pass-event");
        Reload(); Changed?.Invoke(); BackupService.Schedule();
    }

    /// <summary>Replace one phone with another (limited to MaxSwaps per pass, logged).</summary>
    public void Swap(long passId, string oldDeviceId, string newDeviceId, string newModel)
    {
        var pass = Get(passId);
        if (pass.Swaps >= MaxSwaps) throw new InvalidOperationException($"This pass already used its {MaxSwaps} swaps.");
        var newId = Norm(newDeviceId);
        if (ActiveFor(newId) != null) throw new InvalidOperationException("The new phone already has an active pass.");
        if (!pass.IsActiveAt(Now())) throw new InvalidOperationException("This pass has expired.");
        RemovePhone(passId, oldDeviceId, isSwap: true);
        using (var c = Open())
        {
            AddPhoneRow(c, passId, newId, newModel);
            var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE passes SET Swaps = Swaps + 1 WHERE Id=$p;";
            cmd.Parameters.AddWithValue("$p", passId);
            cmd.ExecuteNonQuery();
        }
        Ledger("weekly-pass-event", $"Pass #{passId}: phone swapped in", newId, newModel, 0, "pass-event");
        Reload(); Changed?.Invoke(); BackupService.Schedule();
    }

    // ---------------- phone-side display file (Xama shows it; enforcement stays on this PC) ----------------

    private static byte[] FileKey() => HMACSHA256.HashData(SettlementKey.Current, Encoding.UTF8.GetBytes("shamm-pass-file-v1"));

    public string PassFileJson(WeeklyPass p, string deviceId, string shopName)
    {
        var now = Now();
        var body = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["version"] = 1,
            ["kind"] = "weekly-pass",
            ["passId"] = p.Id,
            ["status"] = !p.ActivePhones.Any(ph => ph.DeviceId == Norm(deviceId)) ? "removed" : p.IsActiveAt(now) ? "active" : "expired",
            ["shopName"] = shopName,
            ["shopCode"] = Closings.ShopCode(Session.ShopId),
            ["deviceId"] = Norm(deviceId),
            ["startUtc"] = p.ActivatedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["expiresUtc"] = p.ExpiresUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["writtenUtc"] = now.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["phonesUsed"] = p.ActivePhones.Count(),
            ["phonesMax"] = MaxPhones,
            ["phones"] = p.ActivePhones.Select(ph => ph.Model).ToArray(),
        };
        var canonical = JsonSerializer.Serialize(body);
        var sig = Convert.ToHexString(HMACSHA256.HashData(FileKey(), Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var full = new SortedDictionary<string, object?>(body, StringComparer.Ordinal) { ["sig"] = sig };
        return JsonSerializer.Serialize(full, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Write the pass file for this phone over MTP (best-effort, off the UI thread).</summary>
    public bool WriteToPhone(MtpService mtp, string mtpId, string deviceId, string shopName)
    {
        var id = Norm(deviceId);
        WeeklyPass? p;
        lock (_gate) p = _cache.LastOrDefault(x => x.Phones.Any(ph => ph.DeviceId == id));
        if (p == null) return false;
        var tmp = Path.Combine(Path.GetTempPath(), $"xama-pass-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tmp, PassFileJson(p, deviceId, shopName), new UTF8Encoding(false));
            var ok = mtp.WritePassFile(mtpId, tmp) > 0;
            try { FamilyGroups.Instance?.WriteToPhone(mtp, mtpId, deviceId, shopName); } catch { }
            return ok;
        }
        catch (Exception ex) { AdbService.Log("pass file write failed: " + ex.Message); return false; }
        finally { try { File.Delete(tmp); } catch { } }
    }
}
