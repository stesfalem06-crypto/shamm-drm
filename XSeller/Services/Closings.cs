using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using XSeller.Models;

namespace XSeller.Services;

/// <summary>
/// Platform agent credential issued by Xama Master (.shammagent). The agent key K_agent =
/// HMAC-SHA256(platformSecret, "agent:" + agentId) is stored encrypted with the agent's
/// passphrase (PBKDF2-SHA256 → AES-256-GCM). The shop never learns K_agent, so it can't
/// produce closing receipt codes that Xama Master would accept.
/// </summary>
public sealed class AgentCredential
{
    public string Format { get; set; } = "shamm-agent";
    public int Version { get; set; } = 1;
    public string AgentId { get; set; } = "";
    public string AgentName { get; set; } = "";
    public DateTime IssuedUtc { get; set; }
    public string SaltB64 { get; set; } = "";
    public int Iterations { get; set; }
    public string NonceB64 { get; set; } = "";
    public string EncKeyB64 { get; set; } = "";   // ciphertext + 16-byte tag
    public string KeyCheck { get; set; } = "";    // hex(HMAC(K_agent, "check"))[..16]

    public static AgentCredential Parse(string json)
    {
        var c = JsonSerializer.Deserialize<AgentCredential>(json) ?? throw new InvalidDataException("Not an agent file.");
        if (c.Format != "shamm-agent" || string.IsNullOrWhiteSpace(c.AgentId)) throw new InvalidDataException("Not an X agent credential (.shammagent).");
        return c;
    }

    /// <summary>Decrypt K_agent with the agent passphrase. Null when the passphrase is wrong.</summary>
    public byte[]? Unlock(string passphrase)
    {
        try
        {
            var kek = Rfc2898DeriveBytes.Pbkdf2(passphrase ?? "", Convert.FromBase64String(SaltB64), Iterations, HashAlgorithmName.SHA256, 32);
            var blob = Convert.FromBase64String(EncKeyB64);
            var key = new byte[blob.Length - 16];
            using var gcm = new AesGcm(kek, 16);
            gcm.Decrypt(Convert.FromBase64String(NonceB64), blob[..^16], blob[^16..], key, Encoding.UTF8.GetBytes(AgentId));
            var check = Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("check")))[..16];
            return string.Equals(check, KeyCheck, StringComparison.OrdinalIgnoreCase) ? key : null;
        }
        catch { return null; }
    }
}

public sealed class EmployeeShare
{
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    public int Count { get; set; }
    public decimal Revenue { get; set; }
    public decimal Share { get; set; }
}

/// <summary>One append-only, hash-chained closing (settlement with a platform agent).</summary>
public sealed class ClosingRecord
{
    public int Number { get; set; }
    public string ShopId { get; set; } = "";
    public string ShopCode { get; set; } = "";
    public string ShopName { get; set; } = "";
    public DateTime PeriodStartUtc { get; set; }
    public DateTime PeriodEndUtc { get; set; }
    public long FirstSaleId { get; set; }
    public long LastSaleId { get; set; }
    public string LastSaleHash { get; set; } = "";
    public int SalesCount { get; set; }
    public decimal Revenue { get; set; }
    public decimal PlatformDue { get; set; }
    public decimal ShopShare { get; set; }
    public decimal EmployeeTotal { get; set; }
    public int PlatformPercent { get; set; }
    public int ShopPercent { get; set; }
    public int EmployeePercent { get; set; }
    public List<EmployeeShare> Employees { get; set; } = new();
    public decimal AmountReceived { get; set; }
    public string AgentId { get; set; } = "";
    public string AgentName { get; set; } = "";
    public string ClosedBy { get; set; } = "";
    public DateTime ClosedAtUtc { get; set; }
    public string PrevHash { get; set; } = "";
    public string RecordHash { get; set; } = "";
    public string ReceiptCode { get; set; } = "";

    public string Canonical() =>
        string.Join('\u001f', Number, ShopId, PeriodStartUtc.ToString("O"), PeriodEndUtc.ToString("O"), FirstSaleId, LastSaleId,
            LastSaleHash, SalesCount, Money(Revenue), Money(PlatformDue), Money(ShopShare), Money(EmployeeTotal),
            $"{PlatformPercent}/{ShopPercent}/{EmployeePercent}",
            string.Join(';', Employees.Select(e => $"{e.UserId}:{e.Count}:{Money(e.Revenue)}:{Money(e.Share)}")),
            Money(AmountReceived), AgentId, ClosedAtUtc.ToString("O"), PrevHash);

    public static string Money(decimal d) => d.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
    public string QrText => $"XSC1;shop={ShopCode};n={Number};amt={Money(AmountReceived)};d={ClosedAtUtc:yyyyMMdd};agent={AgentId};c={ReceiptCode}";
}

/// <summary>
/// Closing / settlement. Receipt code (what the agent hands to the owner with the cash):
///   base32(HMAC-SHA256(K_agent, "XSC1|shopCode|number|amount(0.00)|yyyyMMdd(UTC)|agentId")[..8])[..12]
/// Verified offline by Xama Master, which derives K_agent from its platform secret.
/// </summary>
public sealed class Closings
{
    private readonly LedgerDatabase _ledger;
    public const string Genesis = "GENESIS";

    public Closings(LedgerDatabase ledger)
    {
        _ledger = ledger;
        using var c = Open();
        var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS closings (
              Number INTEGER PRIMARY KEY, Json TEXT NOT NULL, PrevHash TEXT NOT NULL, RecordHash TEXT NOT NULL, ReceiptCode TEXT NOT NULL);
            CREATE TRIGGER IF NOT EXISTS closings_no_update BEFORE UPDATE ON closings
              BEGIN SELECT RAISE(ABORT, 'closing history is append-only'); END;
            CREATE TRIGGER IF NOT EXISTS closings_no_delete BEFORE DELETE ON closings
              BEGIN SELECT RAISE(ABORT, 'closing history is append-only'); END;
            CREATE TABLE IF NOT EXISTS agents (AgentId TEXT PRIMARY KEY, Json TEXT NOT NULL, ImportedUtc TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open() { var c = new SqliteConnection(_ledger.ConnectionString); c.Open(); return c; }

    public static string ShopCode(string shopId) => shopId.Length >= 8 ? shopId[..8].ToUpperInvariant() : shopId.ToUpperInvariant();

    // ---------------- agents
    public void ImportAgent(AgentCredential a)
    {
        using var c = Open();
        var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO agents(AgentId,Json,ImportedUtc) VALUES($i,$j,$t) ON CONFLICT(AgentId) DO UPDATE SET Json=$j;";
        cmd.Parameters.AddWithValue("$i", a.AgentId);
        cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(a));
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public List<AgentCredential> Agents()
    {
        var list = new List<AgentCredential>();
        using var c = Open();
        var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Json FROM agents ORDER BY ImportedUtc;";
        using var r = cmd.ExecuteReader();
        while (r.Read()) { try { list.Add(AgentCredential.Parse(r.GetString(0))); } catch { } }
        return list;
    }

    // ---------------- history
    public List<ClosingRecord> History()
    {
        var list = new List<ClosingRecord>();
        using var c = Open();
        var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Json FROM closings ORDER BY Number;";
        using var r = cmd.ExecuteReader();
        while (r.Read()) { var x = JsonSerializer.Deserialize<ClosingRecord>(r.GetString(0)); if (x != null) list.Add(x); }
        return list;
    }

    public static string HashOf(ClosingRecord r) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(r.Canonical())));

    /// <summary>Problems in the closing chain (empty = intact).</summary>
    public List<string> VerifyHistory()
    {
        var problems = new List<string>();
        var prev = Genesis;
        var expectNum = 1;
        DateTime last = DateTime.MinValue;
        foreach (var r in History())
        {
            if (r.Number != expectNum) problems.Add($"Closing #{expectNum} is missing (history was edited).");
            if (r.PrevHash != prev) problems.Add($"Closing #{r.Number}: chain link broken.");
            if (HashOf(r) != r.RecordHash) problems.Add($"Closing #{r.Number}: contents changed after closing.");
            if (r.ClosedAtUtc < last) problems.Add($"Closing #{r.Number}: dated before the previous closing (clock moved back).");
            last = r.ClosedAtUtc;
            prev = r.RecordHash;
            expectNum = r.Number + 1;
        }
        return problems;
    }

    /// <summary>What is owed since the last closing (the "counter").</summary>
    public ClosingRecord Pending(string shopName)
    {
        var hist = History();
        var lastClose = hist.LastOrDefault();
        var afterId = lastClose?.LastSaleId ?? 0;
        var all = _ledger.GetSales(DateTime.MinValue, DateTime.MaxValue).Where(s => s.Id > afterId).ToList();
        var (pp, sp, ep) = RevenueSplit.Current;
        var rec = new ClosingRecord
        {
            Number = (lastClose?.Number ?? 0) + 1,
            ShopId = Session.ShopId,
            ShopCode = ShopCode(Session.ShopId),
            ShopName = shopName,
            PeriodStartUtc = lastClose?.PeriodEndUtc ?? (all.Count > 0 ? all.Min(s => s.SentAtUtc) : DateTime.UtcNow),
            PeriodEndUtc = DateTime.UtcNow,
            FirstSaleId = all.Count > 0 ? all.Min(s => s.Id) : afterId + 1,
            LastSaleId = all.Count > 0 ? all.Max(s => s.Id) : afterId,
            SalesCount = all.Count,
            PlatformPercent = pp, ShopPercent = sp, EmployeePercent = ep,
            PrevHash = lastClose?.RecordHash ?? Genesis,
        };
        rec.Revenue = all.Sum(s => (decimal)s.TokenPrice);
        var split = RevenueSplit.Split(rec.Revenue);
        rec.PlatformDue = split.Platform;
        rec.ShopShare = split.Shop;
        rec.Employees = all.GroupBy(s => s.UserId).Select(g =>
        {
            var rev = g.Sum(x => (decimal)x.TokenPrice);
            return new EmployeeShare { UserId = g.Key, UserName = g.OrderByDescending(x => x.Id).First().UserName, Count = g.Count(), Revenue = rev, Share = RevenueSplit.Split(rev).Employee };
        }).OrderByDescending(e => e.Revenue).ToList();
        rec.EmployeeTotal = split.Employee;
        rec.LastSaleHash = rec.LastSaleId > 0 ? _ledger.GetSaleHash(rec.LastSaleId) ?? "" : "";
        return rec;
    }

    /// <summary>Finalize: requires the agent's passphrase. Appends the record and returns it with its receipt code.</summary>
    public ClosingRecord Close(ClosingRecord pending, AgentCredential agent, string passphrase, decimal amountReceived)
    {
        var key = agent.Unlock(passphrase) ?? throw new UnauthorizedAccessException("Wrong agent passphrase.");
        try
        {
            if (History().LastOrDefault() is { } last && last.Number + 1 != pending.Number)
                throw new InvalidOperationException("Another closing was recorded meanwhile — reopen the closing screen.");
            pending.AgentId = agent.AgentId;
            pending.AgentName = agent.AgentName;
            pending.AmountReceived = Math.Round(amountReceived, 2);
            pending.ClosedBy = Session.UserName;
            pending.ClosedAtUtc = DateTime.UtcNow;
            pending.RecordHash = HashOf(pending);
            pending.ReceiptCode = ReceiptCode(key, pending.ShopCode, pending.Number, pending.AmountReceived, pending.ClosedAtUtc, pending.AgentId);
            using var c = Open();
            var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO closings(Number,Json,PrevHash,RecordHash,ReceiptCode) VALUES($n,$j,$p,$h,$c);";
            cmd.Parameters.AddWithValue("$n", pending.Number);
            cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(pending));
            cmd.Parameters.AddWithValue("$p", pending.PrevHash);
            cmd.Parameters.AddWithValue("$h", pending.RecordHash);
            cmd.Parameters.AddWithValue("$c", pending.ReceiptCode);
            cmd.ExecuteNonQuery();
            Accounts.Instance.Audit(Session.UserId, $"closing:{pending.Number}:{agent.AgentId}");
            Accounts.Instance.Set("closing_count", pending.Number.ToString());
            BackupService.Schedule();
            _signedCache[pending.Number] = SignedFile(key, pending);
            return pending;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private readonly Dictionary<int, string> _signedCache = new();

    /// <summary>Signed closing file for USB import into Xama Master (only available right after closing,
    /// because signing needs the agent key).</summary>
    public string? SignedClosingFile(int number) => _signedCache.TryGetValue(number, out var s) ? s : null;

    private static string SignedFile(byte[] key, ClosingRecord r)
    {
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(r));
        var mac = Convert.ToHexString(HMACSHA256.HashData(key, payload));
        return JsonSerializer.Serialize(new { Format = "shamm-closing", Version = 1, r.AgentId, Payload = Convert.ToBase64String(payload), HmacSha256 = mac },
            new JsonSerializerOptions { WriteIndented = true });
    }

    public static string ReceiptCode(byte[] key, string shopCode, int number, decimal amount, DateTime closedUtc, string agentId)
    {
        var msg = $"XSC1|{shopCode.ToUpperInvariant()}|{number}|{ClosingRecord.Money(amount)}|{closedUtc:yyyyMMdd}|{agentId}";
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(msg));
        var b32 = Base32(mac[..8])[..12];
        return $"{b32[..4]}-{b32[4..8]}-{b32[8..12]}";
    }

    /// <summary>Crockford base32 (no I, L, O, U — easy to read aloud / type).</summary>
    public static string Base32(byte[] data)
    {
        const string A = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b; bits += 8;
            while (bits >= 5) { sb.Append(A[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(A[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] ReceiptPdf(ClosingRecord r)
    {
        var lines = new List<string>
        {
            "X SELLER  -  CLOSING RECEIPT",
            "",
            $"Shop:            {r.ShopName}   (shop code {r.ShopCode})",
            $"Closing #:       {r.Number}",
            $"Closed:          {r.ClosedAtUtc:yyyy-MM-dd HH:mm} UTC by {r.ClosedBy}",
            $"Agent:           {r.AgentName} ({r.AgentId})",
            $"Period:          {r.PeriodStartUtc:yyyy-MM-dd HH:mm} -> {r.PeriodEndUtc:yyyy-MM-dd HH:mm} UTC",
            $"Sales:           {r.SalesCount}  (sale #{r.FirstSaleId}..#{r.LastSaleId})",
            "",
            $"Revenue:         {ClosingRecord.Money(r.Revenue)}",
            $"Platform {r.PlatformPercent,2}%:    {ClosingRecord.Money(r.PlatformDue)}   (due to platform)",
            $"Shop {r.ShopPercent,2}%:        {ClosingRecord.Money(r.ShopShare)}",
            $"Employees {r.EmployeePercent,2}%:   {ClosingRecord.Money(r.EmployeeTotal)}",
        };
        foreach (var e in r.Employees)
            lines.Add($"   {e.UserName,-24} {e.Count,5} sales  revenue {ClosingRecord.Money(e.Revenue),10}  share {ClosingRecord.Money(e.Share),10}");
        lines.Add("");
        lines.Add($"AMOUNT RECEIVED: {ClosingRecord.Money(r.AmountReceived)}");
        lines.Add("");
        lines.Add($"RECEIPT CODE:    {r.ReceiptCode}");
        lines.Add($"QR text:         {r.QrText}");
        lines.Add($"Record hash:     {r.RecordHash}");
        lines.Add($"Previous hash:   {r.PrevHash}");
        lines.Add("");
        lines.Add("Owner: verify in Xama Master > Platform tools > Verify closing (shop code, #, amount, date, code).");
        return MiniPdf.Text(lines);
    }
}
