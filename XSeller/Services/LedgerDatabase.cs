using Microsoft.Data.Sqlite;
using XSeller.Models;

namespace XSeller.Services;

/// <summary>
/// Local transfer ledger. Lives at %DOCUMENTS%\XSeller\ledger.db.
///
/// SECURITY NOTE: this is a plain SQLite file today, readable by anyone
/// with access to the shop PC. Given the trust model (USB-visit settlement,
/// shop owner has no reason to want to inflate their own debt downward
/// undetected - the agent's copy of history plus repeated visits catches
/// tampering) this is an acceptable starting point, but it is NOT
/// tamper-proof. Phase 5 adds a signed/HMAC-chained ledger (each row's hash
/// includes the previous row's hash) so any edited-in-place row breaks the
/// chain and X Seller can detect and flag it on next launch. Flagging this
/// now rather than silently shipping a weaker guarantee than "secure
/// database" implies.
/// </summary>
public class LedgerDatabase
{
    private readonly string _dbPath;
    private readonly string _connString;

    public LedgerDatabase()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "XSeller");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "ledger.db");
        _connString = $"Data Source={_dbPath}";
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var conn = new SqliteConnection(_connString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS ledger (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                VideoId TEXT NOT NULL,
                VideoTitle TEXT NOT NULL,
                DeviceSerial TEXT NOT NULL,
                DeviceModel TEXT NOT NULL,
                TokenPrice INTEGER NOT NULL,
                SentAtUtc TEXT NOT NULL,
                Settled INTEGER NOT NULL DEFAULT 0
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void RecordTransfer(LedgerEntry entry)
    {
        using var conn = new SqliteConnection(_connString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ledger (VideoId, VideoTitle, DeviceSerial, DeviceModel, TokenPrice, SentAtUtc, Settled)
            VALUES ($vid, $title, $serial, $model, $price, $sent, 0);
            """;
        cmd.Parameters.AddWithValue("$vid", entry.VideoId);
        cmd.Parameters.AddWithValue("$title", entry.VideoTitle);
        cmd.Parameters.AddWithValue("$serial", entry.DeviceSerial);
        cmd.Parameters.AddWithValue("$model", entry.DeviceModel);
        cmd.Parameters.AddWithValue("$price", entry.TokenPrice);
        cmd.Parameters.AddWithValue("$sent", entry.SentAtUtc.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public int GetOutstandingDebt()
    {
        using var conn = new SqliteConnection(_connString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(TokenPrice), 0) FROM ledger WHERE Settled = 0;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Flips the given row IDs to Settled = 1. Returns how many
    /// rows were actually updated (lets the UI confirm nothing was
    /// skipped/missing).</summary>
    public int MarkSettled(List<int> ids)
    {
        if (ids.Count == 0) return 0;
        using var conn = new SqliteConnection(_connString);
        conn.Open();
        using var tx = conn.BeginTransaction();
        var updated = 0;
        foreach (var id in ids)
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE ledger SET Settled = 1 WHERE Id = $id AND Settled = 0;";
            cmd.Parameters.AddWithValue("$id", id);
            updated += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return updated;
    }

    public List<LedgerEntry> GetRecentTransfers(int limit = 50)
    {
        var results = new List<LedgerEntry>();
        using var conn = new SqliteConnection(_connString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, VideoId, VideoTitle, DeviceSerial, DeviceModel, TokenPrice, SentAtUtc, Settled " +
                           "FROM ledger ORDER BY Id DESC LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new LedgerEntry
            {
                Id = reader.GetInt32(0),
                VideoId = reader.GetString(1),
                VideoTitle = reader.GetString(2),
                DeviceSerial = reader.GetString(3),
                DeviceModel = reader.GetString(4),
                TokenPrice = reader.GetInt32(5),
                SentAtUtc = DateTime.Parse(reader.GetString(6)),
                Settled = reader.GetInt32(7) == 1,
            });
        }
        return results;
    }
}
