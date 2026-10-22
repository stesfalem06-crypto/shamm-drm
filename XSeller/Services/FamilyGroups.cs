using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using XSeller.Models;

namespace XSeller.Services;

public sealed class FamilyPhone
{
    public string DeviceId { get; set; } = "";
    public string Model { get; set; } = "";
    public DateTime AddedUtc { get; set; }
    public DateTime? RemovedUtc { get; set; }
    public bool Active => RemovedUtc == null;
}

public sealed class FamilyGroup
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    public int Swaps { get; set; }
    public List<FamilyPhone> Phones { get; set; } = new();
    public IEnumerable<FamilyPhone> ActivePhones => Phones.Where(p => p.Active);
}

/// <summary>
/// Shop-local family groups (max 3 phones, max 3 swaps). When sending protected content,
/// wraps are produced for every active member (see MultiRecipientKey / docs/FAMILY_GROUP.md).
/// If the target phone is on an active weekly pass, that pass's phones are used instead.
/// </summary>
public sealed class FamilyGroups
{
    public const int MaxPhones = 3;
    public const int MaxSwaps = 3;
    public static FamilyGroups? Instance { get; private set; }

    private readonly LedgerDatabase _ledger;
    private readonly object _gate = new();
    private List<FamilyGroup> _cache = new();
    public event Action? Changed;

    public FamilyGroups(LedgerDatabase ledger)
    {
        _ledger = ledger;
        using var c = Open();
        var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS family_groups (
              Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL, CreatedUtc TEXT NOT NULL,
              UserId TEXT NOT NULL, UserName TEXT NOT NULL, Swaps INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS family_phones (
              GroupId INTEGER NOT NULL, DeviceId TEXT NOT NULL, Model TEXT NOT NULL,
              AddedUtc TEXT NOT NULL, RemovedUtc TEXT);
            CREATE INDEX IF NOT EXISTS ix_family_phones ON family_phones(DeviceId);
            CREATE TRIGGER IF NOT EXISTS family_groups_no_delete BEFORE DELETE ON family_groups
              BEGIN SELECT RAISE(ABORT, 'family groups are append-only'); END;
            CREATE TRIGGER IF NOT EXISTS family_phones_no_delete BEFORE DELETE ON family_phones
              BEGIN SELECT RAISE(ABORT, 'family groups are append-only'); END;
            """;
        cmd.ExecuteNonQuery();
        Reload();
        Instance = this;
    }

    private SqliteConnection Open() { var c = new SqliteConnection(_ledger.ConnectionString); c.Open(); return c; }

    public List<FamilyGroup> All() { lock (_gate) return _cache.ToList(); }

    private void Reload()
    {
        var map = new Dictionary<long, FamilyGroup>();
        using var c = Open();
        var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, CreatedUtc, UserId, UserName, Swaps FROM family_groups ORDER BY Id;";
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                map[r.GetInt64(0)] = new FamilyGroup
                {
                    Id = r.GetInt64(0), Name = r.GetString(1),
                    CreatedUtc = DateTime.Parse(r.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime(),
                    UserId = r.GetString(3), UserName = r.GetString(4), Swaps = r.GetInt32(5),
                };
        var p = c.CreateCommand();
        p.CommandText = "SELECT GroupId, DeviceId, Model, AddedUtc, RemovedUtc FROM family_phones ORDER BY rowid;";
        using (var r = p.ExecuteReader())
            while (r.Read())
                if (map.TryGetValue(r.GetInt64(0), out var g))
                    g.Phones.Add(new FamilyPhone
                    {
                        DeviceId = r.GetString(1), Model = r.GetString(2),
                        AddedUtc = DateTime.Parse(r.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime(),
                        RemovedUtc = r.IsDBNull(4) ? null : DateTime.Parse(r.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime(),
                    });
        lock (_gate) _cache = map.Values.ToList();
    }

    public static string Norm(string id) => (id ?? "").Trim().ToLowerInvariant();

    public FamilyGroup? ForDevice(string deviceId)
    {
        var id = Norm(deviceId);
        if (id.Length == 0) return null;
        lock (_gate) return _cache.LastOrDefault(g => g.ActivePhones.Any(p => p.DeviceId == id));
    }

    /// <summary>
    /// Device fingerprints (raw 32-byte) to wrap for when sending to <paramref name="targetDeviceId"/>.
    /// Weekly pass phones win when the target is on an active pass; else dedicated family group; else solo.
    /// </summary>
    public List<(string DeviceIdHex, string Model)> RecipientsFor(string targetDeviceId, string targetModel)
    {
        var id = Norm(targetDeviceId);
        var pass = Passes.Instance?.ActiveFor(id);
        if (pass != null)
            return pass.ActivePhones.Select(p => (p.DeviceId, p.Model)).ToList();
        var fam = ForDevice(id);
        if (fam != null)
            return fam.ActivePhones.Select(p => (p.DeviceId, p.Model)).ToList();
        return new List<(string, string)> { (id, targetModel) };
    }

    private void Ledger(string title, string deviceId, string model)
    {
        lock (TransferService.LedgerGate) _ledger.RecordTransfer(new LedgerEntry
        {
            VideoId = "family-event", VideoTitle = title, DeviceSerial = deviceId, DeviceModel = model,
            TokenPrice = 0, SentAtUtc = DateTime.UtcNow, UserId = Session.UserId, UserName = Session.UserName,
            ShopId = Session.ShopId, Kind = "family-event", Category = "family",
        });
    }

    public FamilyGroup Create(string name, string firstDeviceId, string firstModel)
    {
        var id = Norm(firstDeviceId);
        if (id.Length == 0) throw new InvalidOperationException("Phone not identified — open Xama once on the phone.");
        if (ForDevice(id) != null) throw new InvalidOperationException("This phone is already in a family group.");
        name = string.IsNullOrWhiteSpace(name) ? firstModel : name.Trim();
        long gid;
        using (var c = Open())
        {
            using var tx = c.BeginTransaction();
            var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO family_groups(Name,CreatedUtc,UserId,UserName) VALUES($n,$t,$u,$un); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$u", Session.UserId);
            cmd.Parameters.AddWithValue("$un", Session.UserName);
            gid = (long)cmd.ExecuteScalar()!;
            AddPhoneRow(c, gid, id, firstModel);
            tx.Commit();
        }
        Ledger($"Family #{gid}: created ({name})", id, firstModel);
        Reload(); Changed?.Invoke(); BackupService.Schedule();
        return All().First(g => g.Id == gid);
    }

    /// <summary>Create a family group whose members mirror an active weekly pass (reuse pass phones).</summary>
    public FamilyGroup CreateFromPass(WeeklyPass pass, string? name = null)
    {
        var phones = pass.ActivePhones.ToList();
        if (phones.Count == 0) throw new InvalidOperationException("Pass has no phones.");
        foreach (var p in phones)
            if (ForDevice(p.DeviceId) is { } existing && existing.ActivePhones.Count() > 0)
                throw new InvalidOperationException($"{p.Model} is already in family #{existing.Id}.");
        var label = name ?? $"Pass #{pass.Id} family";
        var first = phones[0];
        var g = Create(label, first.DeviceId, first.Model);
        foreach (var p in phones.Skip(1))
            AddPhone(g.Id, p.DeviceId, p.Model);
        return All().First(x => x.Id == g.Id);
    }

    private static void AddPhoneRow(SqliteConnection c, long groupId, string deviceId, string model)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO family_phones(GroupId,DeviceId,Model,AddedUtc) VALUES($g,$d,$m,$t);";
        cmd.Parameters.AddWithValue("$g", groupId);
        cmd.Parameters.AddWithValue("$d", deviceId);
        cmd.Parameters.AddWithValue("$m", model);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private FamilyGroup Get(long id) => All().FirstOrDefault(g => g.Id == id) ?? throw new InvalidOperationException("Family group not found.");

    public void AddPhone(long groupId, string deviceId, string model)
    {
        var id = Norm(deviceId);
        var g = Get(groupId);
        if (g.ActivePhones.Any(p => p.DeviceId == id)) throw new InvalidOperationException("Phone already in this group.");
        if (ForDevice(id) != null) throw new InvalidOperationException("Phone is already in another family group.");
        if (g.ActivePhones.Count() >= MaxPhones) throw new InvalidOperationException($"A family has at most {MaxPhones} phones — swap one first.");
        using (var c = Open()) AddPhoneRow(c, groupId, id, model);
        Ledger($"Family #{groupId}: phone added", id, model);
        Reload(); Changed?.Invoke(); BackupService.Schedule();
    }

    public void RemovePhone(long groupId, string deviceId, bool isSwap = false)
    {
        var id = Norm(deviceId);
        var g = Get(groupId);
        if (!g.ActivePhones.Any(p => p.DeviceId == id)) throw new InvalidOperationException("Phone is not in this group.");
        if (g.ActivePhones.Count() <= 1 && !isSwap) throw new InvalidOperationException("A family needs at least one phone — use Swap.");
        var model = g.ActivePhones.First(p => p.DeviceId == id).Model;
        using (var c = Open())
        {
            var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE family_phones SET RemovedUtc=$t WHERE GroupId=$g AND DeviceId=$d AND RemovedUtc IS NULL;";
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$g", groupId);
            cmd.Parameters.AddWithValue("$d", id);
            cmd.ExecuteNonQuery();
        }
        Ledger(isSwap ? $"Family #{groupId}: phone swapped out" : $"Family #{groupId}: phone removed", id, model);
        Reload(); Changed?.Invoke(); BackupService.Schedule();
    }

    public void Swap(long groupId, string oldDeviceId, string newDeviceId, string newModel)
    {
        var g = Get(groupId);
        if (g.Swaps >= MaxSwaps) throw new InvalidOperationException($"This family already used its {MaxSwaps} swaps.");
        var newId = Norm(newDeviceId);
        if (ForDevice(newId) != null) throw new InvalidOperationException("The new phone is already in a family group.");
        RemovePhone(groupId, oldDeviceId, isSwap: true);
        using (var c = Open())
        {
            AddPhoneRow(c, groupId, newId, newModel);
            var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE family_groups SET Swaps = Swaps + 1 WHERE Id=$g;";
            cmd.Parameters.AddWithValue("$g", groupId);
            cmd.ExecuteNonQuery();
        }
        Ledger($"Family #{groupId}: phone swapped in", newId, newModel);
        Reload(); Changed?.Invoke(); BackupService.Schedule();
    }

    public void Rename(long groupId, string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) throw new ArgumentException("Enter a name.");
        using var c = Open();
        var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE family_groups SET Name=$n WHERE Id=$g;";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$g", groupId);
        cmd.ExecuteNonQuery();
        Reload(); Changed?.Invoke();
    }

    // ---- signed phone file
    private static byte[] FileKey() => HMACSHA256.HashData(SettlementKey.Current, Encoding.UTF8.GetBytes("shamm-family-file-v1"));

    public string FamilyFileJson(FamilyGroup g, string deviceId, string shopName, string? source = null)
    {
        var id = Norm(deviceId);
        var members = g.ActivePhones.OrderBy(p => p.DeviceId).Select(p => new Dictionary<string, object?>
        {
            ["deviceId"] = p.DeviceId, ["model"] = p.Model,
        }).ToList();
        var body = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["version"] = 1,
            ["kind"] = "family-group",
            ["groupId"] = g.Id,
            ["shopName"] = shopName,
            ["shopCode"] = Closings.ShopCode(Session.ShopId),
            ["status"] = g.ActivePhones.Any(p => p.DeviceId == id) ? "active" : "removed",
            ["deviceId"] = id,
            ["members"] = members,
            ["phonesUsed"] = members.Count,
            ["phonesMax"] = MaxPhones,
            ["writtenUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        };
        if (source != null) body["source"] = source;
        var canonical = JsonSerializer.Serialize(body);
        var sig = Convert.ToHexString(HMACSHA256.HashData(FileKey(), Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var full = new SortedDictionary<string, object?>(body, StringComparer.Ordinal) { ["sig"] = sig };
        return JsonSerializer.Serialize(full, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>JSON for a weekly-pass-backed family (groupId = pass id, source=weekly-pass).</summary>
    public string FamilyFileJsonFromPass(WeeklyPass pass, string deviceId, string shopName)
    {
        var syn = new FamilyGroup
        {
            Id = pass.Id, Name = $"Pass #{pass.Id}",
            Phones = pass.ActivePhones.Select(p => new FamilyPhone { DeviceId = p.DeviceId, Model = p.Model }).ToList(),
        };
        return FamilyFileJson(syn, deviceId, shopName, source: "weekly-pass");
    }

    public bool WriteToPhone(MtpService mtp, string mtpId, string deviceId, string shopName)
    {
        var id = Norm(deviceId);
        string json;
        var pass = Passes.Instance?.ActiveFor(id);
        if (pass != null) json = FamilyFileJsonFromPass(pass, id, shopName);
        else if (ForDevice(id) is { } g) json = FamilyFileJson(g, id, shopName);
        else return false;
        var tmp = Path.Combine(Path.GetTempPath(), $"xama-family-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            return mtp.WriteFamilyFile(mtpId, tmp) > 0;
        }
        catch (Exception ex) { AdbService.Log("family file write failed: " + ex.Message); return false; }
        finally { try { File.Delete(tmp); } catch { } }
    }
}
