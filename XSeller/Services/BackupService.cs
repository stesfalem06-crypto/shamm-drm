using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace XSeller.Services;

/// <summary>
/// Encrypted, multi-location backups of the shop ledger DB. Key is derived inside the app (same
/// SettlementKey family as reports). Locations are hidden folders the shop UI cannot clear.
/// On startup: if the main DB is missing, older, or its hash chain is broken/shorter than a
/// backup, the newest valid backup is restored and the restore is logged.
/// </summary>
public static class BackupService
{
    /// <summary>One-line result of the most recent StartupRestore (shown in closing / diagnostics).</summary>
    public static string LastStartupMessage { get; private set; } = "";
    public const string Ext = ".shammbak";
    private static readonly object Gate = new();
    private static System.Threading.Timer? _timer;
    private static volatile bool _pending;
    private static DateTime _lastOk = DateTime.MinValue;

    public sealed record Slot(string Path, string Label, DateTime? WrittenUtc, long Bytes, bool Ok, string Note)
    {
        public string Age => WrittenUtc == null ? "—" : $"{(DateTime.UtcNow - WrittenUtc.Value).TotalHours:0.#} h ago";
        public string SizeLabel => Bytes <= 0 ? "—" : $"{Bytes / 1024.0:0} KB";
        public string Status => Ok ? "OK" : (string.IsNullOrEmpty(Note) ? "empty" : "bad");
    }

    public sealed record Health(List<Slot> Slots, string Summary, DateTime? LastOkUtc, bool AnyOk)
    {
        public string LastOkLabel => LastOkUtc == null ? "never" : LastOkUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    // ---------------- locations (hidden)
    public static List<(string Dir, string Label)> Locations()
    {
        var list = new List<(string, string)>
        {
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows", "Caches", ".xseller"), "ProgramData"),
        };
        try
        {
            var sys = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\";
            var other = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady && !string.Equals(d.Name, sys, StringComparison.OrdinalIgnoreCase))
                .Select(d => d.Name.TrimEnd('\\')).FirstOrDefault();
            if (!string.IsNullOrEmpty(other))
                list.Add((Path.Combine(other, "System Volume Information", ".xseller"), "Drive " + other.TrimEnd('\\')));
        }
        catch { }
        list.Add((Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "CLR", ".xseller"), "AppData Local"));
        return list;
    }

    private static byte[] Key() => HMACSHA256.HashData(SettlementKey.Current, Encoding.UTF8.GetBytes("shamm-db-backup-v1"));

    private static string FilePath(string dir) => Path.Combine(dir, "ledger" + Ext);

    // ---------------- encrypt / decrypt (AES-256-GCM; header: magic|ver|utc ticks|plainLen|nonce|ct+tag)
    private const string Magic = "SHAMMBAK1";

    public static byte[] Encrypt(byte[] plain)
    {
        var key = Key();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ct = new byte[plain.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(key, 16)) gcm.Encrypt(nonce, plain, ct, tag, Encoding.ASCII.GetBytes(Magic));
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);                  // length-prefixed UTF8
            w.Write((byte)1);
            w.Write(DateTime.UtcNow.Ticks);
            w.Write(plain.Length);
            w.Write(nonce);
            w.Write(ct);
            w.Write(tag);
        }
        return ms.ToArray();
    }

    public static (byte[] Plain, DateTime WrittenUtc)? Decrypt(byte[] blob)
    {
        try
        {
            using var ms = new MemoryStream(blob);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            if (r.ReadString() != Magic) return null;
            if (r.ReadByte() != 1) return null;
            var written = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
            var len = r.ReadInt32();
            var nonce = r.ReadBytes(12);
            var ct = r.ReadBytes(len);
            var tag = r.ReadBytes(16);
            var plain = new byte[len];
            using (var gcm = new AesGcm(Key(), 16)) gcm.Decrypt(nonce, ct, tag, plain, Encoding.ASCII.GetBytes(Magic));
            return (plain, written);
        }
        catch { return null; }
    }

    // ---------------- write
    public static void Schedule()
    {
        _pending = true;
        _timer ??= new System.Threading.Timer(_ =>
        {
            if (!_pending) return;
            _pending = false;
            try { Run(); } catch (Exception ex) { AdbService.Log("backup: " + ex.Message); }
        }, null, TimeSpan.FromSeconds(4), TimeSpan.FromMinutes(30));
        try { _timer.Change(TimeSpan.FromSeconds(4), TimeSpan.FromMinutes(30)); } catch { }
    }

    public static void StartPeriodic() => Schedule();

    /// <summary>Snapshot the live DB into every location. Best-effort; never throws to the caller.</summary>
    public static void Run()
    {
        lock (Gate)
        {
            try
            {
                var src = LedgerDatabase.DefaultPath;
                if (!File.Exists(src)) return;
                // Flush SQLite WAL into the main file, then clear the connection pool so the file is free to copy.
                try
                {
                    using (var c = new SqliteConnection($"Data Source={src}"))
                    {
                        c.Open();
                        using var cmd = c.CreateCommand();
                        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                        cmd.ExecuteNonQuery();
                    }
                    SqliteConnection.ClearAllPools();
                }
                catch (Exception ex) { AdbService.Log("backup checkpoint: " + ex.Message); }
                var plain = File.ReadAllBytes(src);
                if (plain.Length < 100) return;
                var blob = Encrypt(plain);
                foreach (var (dir, label) in Locations())
                {
                    try
                    {
                        Directory.CreateDirectory(dir);
                        try { File.SetAttributes(dir, FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.System); } catch { }
                        var dest = FilePath(dir);
                        var tmp = dest + ".tmp";
                        File.WriteAllBytes(tmp, blob);
                        try { File.SetAttributes(tmp, FileAttributes.Hidden | FileAttributes.System); } catch { }
                        File.Move(tmp, dest, overwrite: true);
                        try { File.SetAttributes(dest, FileAttributes.Hidden | FileAttributes.System); } catch { }
                    }
                    catch (Exception ex) { AdbService.Log($"backup {label}: {ex.Message}"); }
                }
                _lastOk = DateTime.UtcNow;
            }
            catch (Exception ex) { AdbService.Log("backup run: " + ex.Message); }
        }
    }

    // ---------------- health / restore
    public static Health GetHealth()
    {
        var slots = new List<Slot>();
        foreach (var (dir, label) in Locations())
        {
            var path = FilePath(dir);
            if (!File.Exists(path)) { slots.Add(new Slot(path, label, null, 0, false, "empty")); continue; }
            try
            {
                var blob = File.ReadAllBytes(path);
                var dec = Decrypt(blob);
                if (dec == null) { slots.Add(new Slot(path, label, null, blob.Length, false, "decrypt failed")); continue; }
                var score = Score(dec.Value.Plain);
                slots.Add(new Slot(path, label, dec.Value.WrittenUtc, blob.Length, score.Ok, score.Note));
            }
            catch (Exception ex) { slots.Add(new Slot(path, label, null, 0, false, ex.Message)); }
        }
        var any = slots.Any(s => s.Ok);
        var newest = slots.Where(s => s.Ok && s.WrittenUtc != null).OrderByDescending(s => s.WrittenUtc).FirstOrDefault();
        var summary = !any ? "No valid backup." :
            $"Newest valid: {newest!.Label} ({newest.Age}, {newest.SizeLabel}). {slots.Count(s => s.Ok)}/{slots.Count} slots OK.";
        return new Health(slots, summary, newest?.WrittenUtc ?? (_lastOk == DateTime.MinValue ? null : _lastOk), any);
    }

    private sealed record ScoreResult(bool Ok, long LastId, string LastHash, int Closings, DateTime WrittenUtc, string Note);

    private static ScoreResult Score(byte[] plain, DateTime written = default)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "xseller-bak-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            File.WriteAllBytes(tmp, plain);
            using var c = new SqliteConnection($"Data Source={tmp};Mode=ReadOnly");
            c.Open();
            // Must have the ledger table.
            using (var q = c.CreateCommand())
            {
                q.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='ledger';";
                if (q.ExecuteScalar() as string != "ledger") return new(false, 0, "", 0, written, "no ledger table");
            }
            var st = LedgerChain.Verify($"Data Source={tmp};Mode=ReadOnly");
            if (!st.Ok) return new(false, st.LastId, st.LastHash, 0, written, "chain broken: " + string.Join("; ", st.Problems.Take(2)));
            var closings = 0;
            try
            {
                using var q = c.CreateCommand();
                q.CommandText = "SELECT COUNT(*) FROM closings;";
                closings = Convert.ToInt32(q.ExecuteScalar());
            }
            catch { }
            return new(true, st.LastId, st.LastHash, closings, written, $"sales #{st.LastId}, closings {closings}");
        }
        catch (Exception ex) { return new(false, 0, "", 0, written, ex.Message); }
        finally { try { File.Delete(tmp); } catch { } }
    }

    /// <summary>Called once before Accounts/Closings/Passes open the DB. Restores if needed. Returns a one-line status.</summary>
    public static string StartupRestore()
    {
        lock (Gate)
        {
            try
            {
                var main = LedgerDatabase.DefaultPath;
                Directory.CreateDirectory(Path.GetDirectoryName(main)!);
                ScoreResult? mainScore = null;
                if (File.Exists(main))
                {
                    try { mainScore = Score(File.ReadAllBytes(main)); }
                    catch (Exception ex) { mainScore = new(false, 0, "", 0, default, "unreadable: " + ex.Message); }
                }

                // Gather candidates.
                var cands = new List<(string Path, string Label, byte[] Plain, DateTime Written, ScoreResult Score)>();
                foreach (var (dir, label) in Locations())
                {
                    var path = FilePath(dir);
                    if (!File.Exists(path)) continue;
                    try
                    {
                        var dec = Decrypt(File.ReadAllBytes(path));
                        if (dec == null) continue;
                        var sc = Score(dec.Value.Plain, dec.Value.WrittenUtc);
                        if (sc.Ok) cands.Add((path, label, dec.Value.Plain, dec.Value.WrittenUtc, sc));
                    }
                    catch { }
                }
                if (cands.Count == 0)
                {
                    if (mainScore is { Ok: true }) LastStartupMessage = "Main DB OK; no backups yet."; return LastStartupMessage;
                    LastStartupMessage = mainScore == null ? "No DB and no backups (fresh install)." : "Main DB damaged and no valid backup — " + mainScore.Note; return LastStartupMessage;
                }

                var best = cands.OrderByDescending(c => c.Score.LastId)
                    .ThenByDescending(c => c.Score.Closings)
                    .ThenByDescending(c => c.Written).First();

                var need = mainScore == null
                    || !mainScore.Ok
                    || best.Score.LastId > mainScore.LastId
                    || (best.Score.LastId == mainScore.LastId && best.Score.Closings > mainScore.Closings);

                if (!need) { LastStartupMessage = $"Main DB OK (sales #{mainScore!.LastId}); newest backup {best.Label} ({best.Written.ToLocalTime():yyyy-MM-dd HH:mm})."; return LastStartupMessage; }

                // Restore.
                try { SqliteConnection.ClearAllPools(); } catch { }
                var bakOfBroken = main + ".broken-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                try { if (File.Exists(main)) File.Move(main, bakOfBroken, overwrite: true); } catch { }
                try { File.Delete(main + "-wal"); File.Delete(main + "-shm"); } catch { }
                File.WriteAllBytes(main, best.Plain);
                // Flag for the ledger to record after schema/Accounts are up.
                try
                {
                    File.WriteAllText(main + ".restored",
                        $"{DateTime.UtcNow:O}|from={best.Label}|sales=#{best.Score.LastId}|closings={best.Score.Closings}|reason={(mainScore == null ? "missing" : !mainScore.Ok ? "broken:" + mainScore.Note : "shorter/older")}");
                }
                catch { }
                LastStartupMessage = $"RESTORED from {best.Label} (sales #{best.Score.LastId}, closings {best.Score.Closings}). Previous main kept as {Path.GetFileName(bakOfBroken)}."; return LastStartupMessage;
            }
            catch (Exception ex) { LastStartupMessage = "Backup restore failed: " + ex.Message; return LastStartupMessage; }
        }
    }

    /// <summary>If a restore just happened, append a ledger note (called after Accounts is up).</summary>
    public static void LogRestoreIfAny(LedgerDatabase ledger)
    {
        var marker = LedgerDatabase.DefaultPath + ".restored";
        if (!File.Exists(marker)) return;
        try
        {
            var text = File.ReadAllText(marker).Trim();
            File.Delete(marker);
            lock (TransferService.LedgerGate)
            {
                ledger.RecordTransfer(new Models.LedgerEntry
                {
                    VideoId = "db-restore",
                    VideoTitle = "Database restored from backup: " + text,
                    DeviceSerial = "local",
                    DeviceModel = "backup",
                    TokenPrice = 0,
                    SentAtUtc = DateTime.UtcNow,
                    UserId = Session.DefaultUserId,
                    UserName = "System",
                    ShopId = Session.ShopId,
                    Kind = "system",
                    Category = "backup",
                });
            }
            // Don't re-trigger a backup of the just-restored state as "after sale" — Schedule is fine.
        }
        catch (Exception ex) { AdbService.Log("log restore: " + ex.Message); }
    }

    public static string DiagnosticsText()
    {
        var h = GetHealth();
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(LastStartupMessage)) sb.AppendLine("Startup: " + LastStartupMessage);
        sb.AppendLine(h.Summary);
        sb.AppendLine($"Last successful write this session: {h.LastOkLabel}");
        foreach (var s in h.Slots)
            sb.AppendLine($"  [{s.Status}] {s.Label}: {s.Age}, {s.SizeLabel} — {s.Note}");
        sb.AppendLine("(Backups are encrypted; the shop UI cannot clear them.)");
        return sb.ToString();
    }
}
