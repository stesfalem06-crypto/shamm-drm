using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace XSeller.Services;

/// <summary>
/// Hash chain over the sales ledger: RowHash = SHA-256(fields | PrevHash). Editing or
/// deleting any row (even with an external SQLite tool) breaks the chain; the closing
/// records pin the last sale id + hash so truncating the tail is detectable too.
/// Also tracks the highest clock value seen to flag clocks moved backwards.
/// </summary>
public static class LedgerChain
{
    public const string Genesis = "GENESIS";

    private const string Cols = "Id, VideoId, VideoTitle, DeviceSerial, DeviceModel, TokenPrice, SentAtUtc, UserId, UserName, ShopId, Kind, Category, PrevHash, RowHash";

    public static string HashRow(SqliteDataReader r, string prev)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 12; i++) sb.Append(r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)).Append('\u001f');
        sb.Append(prev);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>Hash any rows that have no RowHash yet (rows from before this feature, then each new row).</summary>
    public static void Backfill(SqliteConnection conn)
    {
        var rows = new List<(long Id, string Hash, string Prev)>();
        var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM ledger ORDER BY Id;";
        string prev = Genesis;
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                if (!r.IsDBNull(13)) { prev = r.GetString(13); continue; }
                var h = HashRow(r, prev);
                rows.Add((r.GetInt64(0), h, prev));
                prev = h;
            }
        }
        foreach (var (id, hash, p) in rows)
        {
            var u = conn.CreateCommand();
            u.CommandText = "UPDATE ledger SET PrevHash=$p, RowHash=$h WHERE Id=$i AND RowHash IS NULL;";
            u.Parameters.AddWithValue("$p", p);
            u.Parameters.AddWithValue("$h", hash);
            u.Parameters.AddWithValue("$i", id);
            u.ExecuteNonQuery();
        }
    }

    public static void SealLast(SqliteConnection conn) => Backfill(conn);

    public sealed record ChainStatus(bool Ok, int Rows, long LastId, string LastHash, List<string> Problems);

    public static ChainStatus Verify(string connString)
    {
        var problems = new List<string>();
        using var conn = new SqliteConnection(connString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM ledger ORDER BY Id;";
        string prev = Genesis;
        long lastId = 0;
        var n = 0;
        DateTime lastTime = DateTime.MinValue;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            n++;
            var id = r.GetInt64(0);
            var storedPrev = r.IsDBNull(12) ? "" : r.GetString(12);
            var stored = r.IsDBNull(13) ? "" : r.GetString(13);
            if (storedPrev != prev) problems.Add($"Sale #{id}: chain link broken (a row before it was deleted or edited).");
            var expect = HashRow(r, storedPrev);
            if (stored != expect) problems.Add($"Sale #{id}: contents changed after it was recorded.");
            if (DateTime.TryParse(r.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t))
            {
                t = t.ToUniversalTime();
                if (lastTime != DateTime.MinValue && t < lastTime.AddMinutes(-10)) problems.Add($"Sale #{id}: time goes backwards ({t:yyyy-MM-dd HH:mm} UTC) — PC clock was moved back.");
                if (t > lastTime) lastTime = t;
            }
            prev = stored;
            lastId = id;
            if (problems.Count > 50) { problems.Add("…more problems not shown."); break; }
        }
        // Tail truncation check against the pinned head.
        try
        {
            var head = Accounts.Instance.Get("ledger_head");
            if (!string.IsNullOrEmpty(head))
            {
                var parts = head.Split('|');
                if (parts.Length == 2 && long.TryParse(parts[0], out var hid) && hid > lastId)
                    problems.Add($"Sales after #{lastId} up to #{hid} are missing (rows deleted).");
            }
        }
        catch { }
        return new ChainStatus(problems.Count == 0, n, lastId, prev, problems);
    }

    /// <summary>Remember the chain head and the highest clock time seen.</summary>
    public static void NoteTime(DateTime utc)
    {
        try
        {
            var a = Accounts.Instance;
            if (DateTime.TryParse(a.Get("max_seen_utc"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var max) && max.ToUniversalTime() >= utc) { }
            else a.Set("max_seen_utc", utc.ToString("O"));
        }
        catch { }
    }

    public static void PinHead(string connString)
    {
        try
        {
            var st = Verify(connString);
            Accounts.Instance.Set("ledger_head", $"{st.LastId}|{st.LastHash}");
        }
        catch { }
    }

    /// <summary>Non-null warning when the PC clock is earlier than a time we've already seen.</summary>
    public static string? ClockWarning()
    {
        try
        {
            var a = Accounts.Instance;
            if (DateTime.TryParse(a.Get("max_seen_utc"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var max))
            {
                max = max.ToUniversalTime();
                if (DateTime.UtcNow < max - TimeSpan.FromMinutes(10))
                    return $"PC clock is behind the last recorded time ({max.ToLocalTime():yyyy-MM-dd HH:mm}). The clock was moved backwards.";
            }
            NoteTime(DateTime.UtcNow);
        }
        catch { }
        return null;
    }
}
