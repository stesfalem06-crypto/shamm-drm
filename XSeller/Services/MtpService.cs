using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaDevices;

namespace XSeller.Services;

/// <summary>A phone seen through Windows Portable Devices (MTP), independent of adb.</summary>
public sealed record MtpPhone(
    string Id, string Name, string Model, string Manufacturer, string Serial,
    bool StorageReady, string? StorageRoot, string? Error);

/// <summary>
/// Contents of the device-id file Xama writes on the phone (see docs/XAMA_DEVICE_FILE.md).
/// </summary>
public sealed class XamaDeviceFile
{
    public int Schema { get; set; }
    public string App { get; set; } = "";
    public string AndroidId { get; set; } = "";
    public string Abi { get; set; } = "";
    public string FingerprintSha256 { get; set; } = "";
    public string Model { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public string WrittenAtUtc { get; set; } = "";
}

/// <summary>
/// MTP/WPD phone discovery + transfer via the MediaDevices library. This is the
/// primary path: it needs NO USB debugging and NO adb drivers — only the phone in
/// "File transfer" mode and unlocked (Windows ships the MTP driver in-box).
/// </summary>
public sealed class MtpService : IDisposable
{
    public const string XamaPackage = "com.shammapps.xama";

    /// <summary>Where Xama writes its device-id file (relative to the storage root), in lookup order.</summary>
    public static readonly string[] DeviceFileCandidates =
    [
        @"Android\media\com.shammapps.xama\xama-device.json",
        @"Download\Xama\.xama-device.json",
        @"Download\Xama\xama-device.json",
    ];

    /// <summary>Protected packages: Xama's own media dir first (readable by Xama on Android 11+ without permissions).</summary>
    public static readonly string[] ProtectedDirs =
    [
        @"Android\media\com.shammapps.xama\incoming",
        @"Download\XamaIncoming",
    ];

    private readonly object _pollLock = new();
    private readonly Dictionary<string, MtpPhone> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object> _deviceLocks = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private volatile List<MtpPhone> _last = new();

    public event Action<List<MtpPhone>>? PhonesChanged;
    public IReadOnlyList<MtpPhone> Last => _last;
    public string LastError { get; private set; } = "";

    public bool IsReady(string id) => _last.Any(p => p.Id == id && p.StorageReady);

    public void Start(int intervalMs = 2000)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var t = new Thread(() =>
        {
            AdbService.Log("MTP watcher start");
            while (!token.IsCancellationRequested)
            {
                Poll();
                token.WaitHandle.WaitOne(intervalMs);
            }
        })
        { IsBackground = true, Name = "mtp-watcher" };
        t.Start();
    }

    /// <summary>USB plug/unplug: poll now and again shortly (storage appears after unlock/mode switch).</summary>
    public void Trigger() => Task.Run(() =>
    {
        Poll();
        Thread.Sleep(1500);
        Poll(recheckAll: true);
    });

    private static bool LooksLikePhone(MediaDevice d)
    {
        // Exclude USB sticks/cards that WPD also exposes (SWD\WPDBUSENUM\...).
        var id = d.DeviceId ?? "";
        if (id.Contains("wpdbusenum", StringComparison.OrdinalIgnoreCase) &&
            !id.Contains("usb#vid_", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    public void Poll(bool recheckAll = false)
    {
        if (!Monitor.TryEnter(_pollLock, 10_000)) return;
        try
        {
            List<MediaDevice> devices;
            try { devices = MediaDevice.GetDevices().ToList(); LastError = ""; }
            catch (Exception ex)
            {
                LastError = ex.Message;
                AdbService.Log("WPD enumerate failed: " + ex.Message);
                return;
            }
            var now = new List<MtpPhone>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in devices)
            {
                try
                {
                    if (!LooksLikePhone(d)) continue;
                    var id = d.DeviceId;
                    seen.Add(id);
                    if (!recheckAll && _known.TryGetValue(id, out var prev) && prev.StorageReady)
                    {
                        now.Add(prev);
                        continue;
                    }
                    now.Add(_known[id] = Probe(d));
                }
                catch (Exception ex) { AdbService.Log("WPD probe error: " + ex.Message); }
                finally { try { d.Dispose(); } catch { } }
            }
            foreach (var k in _known.Keys.Where(k => !seen.Contains(k)).ToList()) _known.Remove(k);

            var changed = now.Count != _last.Count ||
                          now.Any(n => !_last.Any(l => l.Id == n.Id && l.StorageReady == n.StorageReady && l.Name == n.Name));
            _last = now;
            if (changed)
            {
                AdbService.Log("MTP phones: " + (now.Count == 0 ? "(none)" :
                    string.Join(", ", now.Select(p => $"{p.Name} ready={p.StorageReady} root={p.StorageRoot} serial={p.Serial} err={p.Error}"))));
                try { PhonesChanged?.Invoke(now); } catch (Exception ex) { AdbService.Log("PhonesChanged: " + ex); }
            }
        }
        finally { Monitor.Exit(_pollLock); }
    }

    private static string Safe(Func<string?> f) { try { return f() ?? ""; } catch { return ""; } }

    private static MtpPhone Probe(MediaDevice d)
    {
        var name = Safe(() => d.FriendlyName);
        if (string.IsNullOrWhiteSpace(name)) name = Safe(() => d.Description);
        var model = Safe(() => d.Model);
        var manu = Safe(() => d.Manufacturer);
        var serial = Safe(() => d.SerialNumber);
        if (string.IsNullOrWhiteSpace(name)) name = string.IsNullOrWhiteSpace(model) ? "Phone" : model;
        try
        {
            d.Connect();
            try
            {
                var root = FindStorageRoot(d);
                return new MtpPhone(d.DeviceId, name, model, manu, serial, root != null, root, null);
            }
            finally { try { d.Disconnect(); } catch { } }
        }
        catch (Exception ex)
        {
            return new MtpPhone(d.DeviceId, name, model, manu, serial, false, null, ex.Message);
        }
    }

    /// <summary>"\Internal storage" (localized names vary) — null when the phone is locked / charge-only.</summary>
    private static string? FindStorageRoot(MediaDevice d)
    {
        MediaDriveInfo[] drives;
        try { drives = d.GetDrives() ?? Array.Empty<MediaDriveInfo>(); } catch { drives = Array.Empty<MediaDriveInfo>(); }
        string? pick = null;
        foreach (var dr in drives)
        {
            string full;
            try { full = dr.RootDirectory.FullName; } catch { continue; }
            var label = Safe(() => dr.Name) + " " + Safe(() => dr.VolumeLabel);
            if (pick == null) pick = full;
            if (label.Contains("Internal", StringComparison.OrdinalIgnoreCase) ||
                label.Contains("Phone", StringComparison.OrdinalIgnoreCase)) { pick = full; break; }
        }
        if (pick != null) return pick;
        // Some devices expose storage only as root children.
        try
        {
            var dirs = d.GetDirectories(@"\");
            return dirs.Length > 0 ? dirs[0] : null;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------ device operations

    private object LockFor(string id)
    {
        lock (_deviceLocks)
        {
            if (!_deviceLocks.TryGetValue(id, out var l)) _deviceLocks[id] = l = new object();
            return l;
        }
    }

    /// <summary>Connect to one phone, run <paramref name="action"/>, disconnect. Serialized per phone.</summary>
    public T WithDevice<T>(string id, Func<MediaDevice, string, T> action)
    {
        lock (LockFor(id))
        {
            MediaDevice? dev = null;
            var all = MediaDevice.GetDevices().ToList();
            try
            {
                dev = all.FirstOrDefault(x => string.Equals(x.DeviceId, id, StringComparison.OrdinalIgnoreCase))
                      ?? throw new IOException("Phone disconnected.");
                dev.Connect();
                try
                {
                    var root = FindStorageRoot(dev) ?? throw new IOException("Phone storage not visible — unlock the phone and choose File transfer.");
                    return action(dev, root);
                }
                finally { try { dev.Disconnect(); } catch { } }
            }
            finally
            {
                foreach (var x in all) { try { x.Dispose(); } catch { } }
            }
        }
    }

    private static string Join(string root, string rel) => root.TrimEnd('\\') + "\\" + rel.TrimStart('\\');

    public static void EnsureDir(MediaDevice dev, string fullPath)
    {
        if (dev.DirectoryExists(fullPath)) return;
        var parts = fullPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var cur = "";
        foreach (var p in parts)
        {
            cur += "\\" + p;
            if (!dev.DirectoryExists(cur)) dev.CreateDirectory(cur);
        }
    }

    /// <summary>Upload (overwrite) one local file with byte progress.</summary>
    public static void Upload(MediaDevice dev, string localPath, string remoteDir, string remoteName, Action<double>? progress)
    {
        EnsureDir(dev, remoteDir);
        var remote = remoteDir.TrimEnd('\\') + "\\" + remoteName;
        if (dev.FileExists(remote)) dev.DeleteFile(remote);
        using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using var ps = new ProgressStream(fs, progress);
        dev.UploadFile(ps, remote);
    }

    /// <summary>Plain destination folder by type: Movies/Xama, Music/Xama, else Download/Xama.</summary>
    public static string PlainDirFor(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        string[] video = [".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".ts", ".flv", ".3gp"];
        string[] audio = [".mp3", ".m4a", ".aac", ".wav", ".flac", ".ogg", ".opus", ".wma", ".amr"];
        if (video.Contains(ext)) return @"Movies\Xama";
        if (audio.Contains(ext)) return @"Music\Xama";
        return @"Download\Xama";
    }

    public void SendPlain(string id, string localPath, Action<double>? progress) =>
        WithDevice(id, (dev, root) =>
        {
            var name = Path.GetFileName(localPath);
            Upload(dev, localPath, Join(root, PlainDirFor(name)), name, progress);
            return true;
        });

    /// <summary>Push a protected package part into the first writable protected dir.</summary>
    public void PushProtectedPart(string id, string localPath, string remoteName, Action<double>? progress) =>
        WithDevice(id, (dev, root) =>
        {
            Exception? last = null;
            foreach (var rel in ProtectedDirs)
            {
                try
                {
                    Upload(dev, localPath, Join(root, rel), remoteName, progress);
                    return true;
                }
                catch (Exception ex) { last = ex; AdbService.Log($"MTP protected push to {rel} failed: {ex.Message}"); }
            }
            throw last ?? new IOException("No writable incoming folder on phone.");
        });

    /// <summary>Read Xama's device-id file. Null when Xama hasn't written it yet.</summary>
    public XamaDeviceFile? ReadDeviceFile(string id) =>
        WithDevice(id, (dev, root) =>
        {
            foreach (var rel in DeviceFileCandidates)
            {
                var path = Join(root, rel);
                try
                {
                    if (!dev.FileExists(path)) continue;
                    using var ms = new MemoryStream();
                    dev.DownloadFile(path, ms);
                    var json = Encoding.UTF8.GetString(ms.ToArray()).TrimStart('\uFEFF');
                    var f = JsonSerializer.Deserialize<XamaDeviceFile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (f != null && !string.IsNullOrWhiteSpace(f.AndroidId)) return f;
                }
                catch (Exception ex) { AdbService.Log($"device file {rel}: {ex.Message}"); }
            }
            return (XamaDeviceFile?)null;
        });

    /// <summary>
    /// SHA-256(normalize(androidId) + "|" + normalize(abi)) — identical to
    /// AdbService.GetHardwareFingerprint and Xama DeviceFingerprint.kt.
    /// Verifies fingerprintSha256 from the file when present.
    /// </summary>
    public static byte[] FingerprintFrom(XamaDeviceFile f)
    {
        static string N(string? raw)
        {
            var s = (raw ?? "").Trim().Trim('\r', '\n');
            if (s.Length == 0 || s.Equals("null", StringComparison.OrdinalIgnoreCase) || s.Equals("unknown", StringComparison.OrdinalIgnoreCase)) return "";
            return s;
        }
        var fp = SHA256.HashData(Encoding.UTF8.GetBytes($"{N(f.AndroidId)}|{N(f.Abi)}"));
        if (!string.IsNullOrWhiteSpace(f.FingerprintSha256) &&
            !string.Equals(Convert.ToHexString(fp), f.FingerprintSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Xama device file is inconsistent (fingerprint mismatch) — open Xama again to refresh it.");
        return fp;
    }

    /// <summary>Enumerate the whole phone storage (for phone-as-root indexing).</summary>
    public List<(string Path, ulong Size, DateTime? Modified)> EnumerateAll(string id, int max, CancellationToken ct) =>
        WithDevice(id, (dev, root) =>
        {
            var list = new List<(string, ulong, DateTime?)>();
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0 && list.Count < max && !ct.IsCancellationRequested)
            {
                var dir = stack.Pop();
                MediaDirectoryInfo di;
                try { di = dev.GetDirectoryInfo(dir); } catch { continue; }
                IEnumerable<MediaFileSystemInfo> entries;
                try { entries = di.EnumerateFileSystemInfos().ToList(); } catch { continue; }
                foreach (var e in entries)
                {
                    if (e is MediaDirectoryInfo) stack.Push(e.FullName);
                    else list.Add((e.FullName, e.Length, e.LastWriteTime));
                }
            }
            return list;
        });

    public void Download(string id, string remotePath, string localPath) =>
        WithDevice(id, (dev, _) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            using var fs = File.Create(localPath);
            dev.DownloadFile(remotePath, fs);
            return true;
        });

    /// <summary>Text dump for Diagnostics.</summary>
    public string DiagnosticsText()
    {
        var sb = new StringBuilder();
        try
        {
            var all = MediaDevice.GetDevices().ToList();
            sb.AppendLine($"WPD devices: {all.Count}");
            foreach (var d in all)
            {
                using (d)
                {
                    sb.AppendLine($"- {Safe(() => d.FriendlyName)} | desc={Safe(() => d.Description)} | model={Safe(() => d.Model)} | manu={Safe(() => d.Manufacturer)} | serial={Safe(() => d.SerialNumber)} | protocol={Safe(() => d.Protocol)} | phoneLike={LooksLikePhone(d)}");
                    sb.AppendLine($"  id={Safe(() => d.DeviceId)}");
                    try
                    {
                        d.Connect();
                        try
                        {
                            foreach (var dr in d.GetDrives() ?? Array.Empty<MediaDriveInfo>())
                                sb.AppendLine($"  drive: {Safe(() => dr.Name)} label={Safe(() => dr.VolumeLabel)} root={Safe(() => dr.RootDirectory.FullName)} free={dr.AvailableFreeSpace}");
                            var root = FindStorageRoot(d);
                            sb.AppendLine($"  storage root: {root ?? "(none — locked / charge-only?)"}");
                            if (root != null)
                            {
                                foreach (var rel in DeviceFileCandidates)
                                {
                                    var p = Join(root, rel);
                                    var exists = false;
                                    try { exists = d.FileExists(p); } catch { }
                                    sb.AppendLine($"  {(exists ? "FOUND  " : "missing")} {rel}");
                                }
                            }
                        }
                        finally { try { d.Disconnect(); } catch { } }
                    }
                    catch (Exception ex) { sb.AppendLine("  connect failed: " + ex.Message); }
                }
            }
        }
        catch (Exception ex) { sb.AppendLine("WPD enumeration failed: " + ex); }
        sb.AppendLine("Watcher view: " + string.Join("; ", _last.Select(p => $"{p.Name} ready={p.StorageReady} err={p.Error}")));
        return sb.ToString();
    }

    public void Dispose() { try { _cts?.Cancel(); } catch { } }
}

/// <summary>Read-through stream that reports fraction read (for per-file upload progress).</summary>
internal sealed class ProgressStream : Stream
{
    private readonly Stream _inner;
    private readonly Action<double>? _progress;
    private long _read;
    private long _lastReport;
    public ProgressStream(Stream inner, Action<double>? progress) { _inner = inner; _progress = progress; }
    public override bool CanRead => true;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = _inner.Read(buffer, offset, count);
        _read += n;
        if (_progress != null && Length > 0 && (_read - _lastReport > 512 * 1024 || n == 0))
        {
            _lastReport = _read;
            _progress(Math.Min(1.0, (double)_read / Length));
        }
        return n;
    }
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
