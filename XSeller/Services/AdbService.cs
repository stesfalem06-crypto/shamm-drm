using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace XSeller.Services;

public record ConnectedDevice(string Serial, string Model, string State);

/// <summary>
/// ADB bridge. Continuously watches for phones; once the user taps "Allow"
/// on the RSA prompt we auto-apply shop-friendly settings.
///
/// Robustness notes (phones-not-listed fix):
/// <list type="bullet">
/// <item><c>adb start-server</c> forks a long-lived daemon that inherits the child's
/// stdout/stderr handles. Reading those redirected pipes to EOF therefore blocks
/// forever, which froze the watcher before its first poll. start-server is now run
/// WITHOUT redirection, and every other call uses event-based reads plus a hard
/// timeout that kills a hung adb.</item>
/// <item>adb is resolved from bundled platform-tools, then PATH, then the Android SDK.</item>
/// <item>Every poll result / error is published via <see cref="StatusChanged"/> and
/// logged to %DOCUMENTS%\XSeller\adb.log.</item>
/// </list>
/// Note: Android does NOT allow a PC to turn on USB debugging the first time
/// without Developer options — that is a platform security rule.
/// </summary>
public class AdbService : IDisposable
{
    private string? _adbPath;
    private CancellationTokenSource? _watchCts;
    private volatile List<ConnectedDevice> _last = new();
    private volatile bool _serverOk;
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private static readonly object LogLock = new();

    public event Action<List<ConnectedDevice>>? DevicesChanged;
    /// <summary>Human-readable one-line adb status for the phone panel.</summary>
    public event Action<string>? StatusChanged;

    public string? AdbPath => _adbPath;
    public bool AdbAvailable => _adbPath != null;
    public string LastStatus { get; private set; } = "adb: starting…";

    public static string LogPath => Path.Combine(FirstRunBootstrap.DocsRoot, "adb.log");

    public AdbService()
    {
        _adbPath = ResolveAdb();
    }

    // ------------------------------------------------------------ locate adb

    public static IEnumerable<string> AdbCandidates()
    {
        var baseDir = AppContext.BaseDirectory;
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? baseDir;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(exeDir, "platform-tools", "adb.exe");
        yield return Path.Combine(baseDir, "platform-tools", "adb.exe");
        yield return Path.Combine(exeDir, "adb.exe");
        yield return Path.Combine(local, "XSeller", "platform-tools", "adb.exe");
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            string c;
            try { c = Path.Combine(dir.Trim().Trim('"'), "adb.exe"); } catch { continue; }
            yield return c;
        }
        foreach (var env in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
        {
            var v = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrWhiteSpace(v)) yield return Path.Combine(v, "platform-tools", "adb.exe");
        }
        yield return Path.Combine(local, "Android", "Sdk", "platform-tools", "adb.exe");
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(pf86)) yield return Path.Combine(pf86, "Android", "android-sdk", "platform-tools", "adb.exe");
    }

    public static string? ResolveAdb()
    {
        foreach (var c in AdbCandidates())
        {
            try { if (File.Exists(c)) return Path.GetFullPath(c); } catch { }
        }
        return null;
    }

    // ------------------------------------------------------------ logging / status

    public static void Log(string msg)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(FirstRunBootstrap.DocsRoot);
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 1_000_000)
                {
                    try { File.Move(LogPath, LogPath + ".old", overwrite: true); } catch { }
                }
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {msg}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break the app */ }
    }

    private void SetStatus(string s)
    {
        if (s == LastStatus) return;
        LastStatus = s;
        Log("STATUS " + s);
        try { StatusChanged?.Invoke(s); } catch { }
    }

    // ------------------------------------------------------------ server

    /// <summary>
    /// Start the adb daemon. Deliberately NOT redirected: the daemon inherits the
    /// child's handles, so redirected pipes would never reach EOF.
    /// </summary>
    public bool StartServer(bool restart = false)
    {
        if (_adbPath == null) _adbPath = ResolveAdb();
        if (_adbPath == null)
        {
            _serverOk = false;
            Log("adb.exe not found. Looked in: " + string.Join("; ", AdbCandidates().Distinct()));
            SetStatus("adb not found — reinstall X Seller (platform-tools folder missing next to XSeller.exe)");
            return false;
        }
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                // A different adb (phone suites, old SDKs) on port 5037 makes ours fail or
                // keeps killing our server ("server version doesn't match"). Remove it.
                if (attempt == 2 || restart || !_foreignChecked) KillForeignAdb(killOurs: attempt == 2 || restart);
                _foreignChecked = true;
                var code = RunNoRedirect("start-server", 30_000);
                Log($"start-server attempt {attempt} exit={code} path={_adbPath}");
                // Verify the server actually answers.
                Run("devices", 15_000);
                _serverOk = true;
                return true;
            }
            catch (Exception ex)
            {
                _serverOk = false;
                Log($"start-server attempt {attempt} failed: {ex.Message}");
                SetStatus($"adb: {_adbPath} | server not responding: {ex.Message}");
            }
        }
        return false;
    }

    private bool _foreignChecked;

    /// <summary>Kill adb.exe processes that are not ours (and optionally ours too).</summary>
    public void KillForeignAdb(bool killOurs)
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("adb"))
            {
                using (p)
                {
                    string? path = null;
                    try { path = p.MainModule?.FileName; } catch { /* access denied for elevated procs */ }
                    var ours = path != null && string.Equals(Path.GetFullPath(path), _adbPath, StringComparison.OrdinalIgnoreCase);
                    if (ours && !killOurs) continue;
                    try
                    {
                        p.Kill(true);
                        Log($"killed adb pid={p.Id} path={path ?? "?"} ({(ours ? "ours, restart" : "foreign")})");
                    }
                    catch (Exception ex) { Log($"could not kill adb pid={p.Id} path={path ?? "?"}: {ex.Message}"); }
                }
            }
        }
        catch (Exception ex) { Log("KillForeignAdb: " + ex.Message); }
    }

    /// <summary>Poll right now (USB plug/unplug event).</summary>
    public void TriggerPoll() => Task.Run(() =>
    {
        PollOnce(force: false);
        Thread.Sleep(1500); // devices often need a moment to enumerate as ADB
        PollOnce(force: false);
    });

    private int RunNoRedirect(string args, int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _adbPath!,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(_adbPath!) ?? "",
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start adb.");
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch { }
            throw new TimeoutException($"adb {args} timed out");
        }
        return p.ExitCode;
    }

    // ------------------------------------------------------------ watcher

    public void StartWatching(int intervalMs = 1000)
    {
        StopWatching();
        _watchCts = new CancellationTokenSource();
        var token = _watchCts.Token;
        var t = new Thread(() =>
        {
            Log($"watcher start, adb={_adbPath ?? "(none)"}, base={AppContext.BaseDirectory}, exe={Environment.ProcessPath}");
            StartServer();
            while (!token.IsCancellationRequested)
            {
                PollOnce();
                try { token.WaitHandle.WaitOne(intervalMs); } catch { break; }
            }
        })
        { IsBackground = true, Name = "adb-watcher" };
        t.Start();
    }

    /// <summary>Restart the adb server and poll immediately (Refresh button).</summary>
    public void Refresh()
    {
        Task.Run(() =>
        {
            SetStatus("adb: restarting server…");
            _adbPath = ResolveAdb();
            StartServer(restart: true);
            PollOnce(force: true);
        });
    }

    private void PollOnce(bool force = false)
    {
        if (!_pollGate.Wait(force ? 15_000 : 3_000)) return;
        try
        {
            if (_adbPath == null || !_serverOk)
            {
                // Retry server start every poll until it works (e.g. adb unpacked later).
                if (!StartServer()) { Publish(new List<ConnectedDevice>(), force); return; }
            }
            List<ConnectedDevice> devices;
            try
            {
                devices = ListDevicesOrThrow();
            }
            catch (Exception ex)
            {
                Log("devices -l failed: " + ex.Message);
                SetStatus($"adb: {_adbPath} | error: {ex.Message}");
                _serverOk = false;
                return;
            }
            var ready = devices.Count(d => d.State == "device");
            SetStatus($"adb: {_adbPath} | server running | {devices.Count} device(s)" +
                      (devices.Count > ready ? $", {ready} ready" : ""));
            Publish(devices, force);
        }
        catch (Exception ex) { Log("poll error: " + ex); }
        finally { _pollGate.Release(); }
    }

    private void Publish(List<ConnectedDevice> devices, bool force)
    {
        var last = _last;
        var changed = force || !devices.Select(d => d.Serial + "|" + d.State + "|" + d.Model).ToHashSet()
            .SetEquals(last.Select(d => d.Serial + "|" + d.State + "|" + d.Model));
        if (!changed) return;
        var newlyAuthorized = devices
            .Where(d => d.State == "device")
            .Where(d => last.All(x => x.Serial != d.Serial) ||
                        last.Any(x => x.Serial == d.Serial && x.State != "device"))
            .ToList();
        _last = devices;
        Log("devices: " + (devices.Count == 0 ? "(none)" : string.Join(", ", devices.Select(d => $"{d.Serial}={d.State}/{d.Model}"))));
        try { DevicesChanged?.Invoke(devices); } catch (Exception ex) { Log("DevicesChanged handler: " + ex); }
        foreach (var d in newlyAuthorized)
            TryOptimizeDevice(d.Serial);
    }

    /// <summary>Last state seen by the watcher (thread-safe snapshot).</summary>
    public IReadOnlyList<ConnectedDevice> LastDevices => _last;

    /// <summary>True when the watcher last saw this serial as authorized ("device").</summary>
    public bool IsReady(string serial) => _last.Any(d => d.Serial == serial && d.State == "device");

    public void StopWatching()
    {
        try { _watchCts?.Cancel(); } catch { }
        _watchCts = null;
    }

    public List<ConnectedDevice> ListDevices()
    {
        try { return ListDevicesOrThrow(); } catch { return new List<ConnectedDevice>(); }
    }

    private List<ConnectedDevice> ListDevicesOrThrow()
    {
        var output = Run("devices -l", 15_000);
        return ParseDevices(output);
    }

    private static readonly Regex DeviceLine = new(
        @"^(?<serial>\S+)\s+(?<state>device|unauthorized|offline|recovery|sideload|bootloader|rescue|authorizing|connecting|host|no permissions|unknown)\b(?<rest>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Parse `adb devices -l` (CRLF/LF, tabs or spaces, daemon chatter, multi-word states).</summary>
    public static List<ConnectedDevice> ParseDevices(string output)
    {
        var devices = new List<ConnectedDevice>();
        foreach (var raw in output.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("*") ||
                line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("adb ", StringComparison.OrdinalIgnoreCase)) continue;
            var m = DeviceLine.Match(line);
            if (!m.Success) continue;
            var serial = m.Groups["serial"].Value;
            var state = m.Groups["state"].Value.ToLowerInvariant();
            var rest = m.Groups["rest"].Value;
            string? Field(string key)
            {
                var mm = Regex.Match(rest, @"(?:^|\s)" + key + @":(\S+)");
                return mm.Success ? mm.Groups[1].Value.Replace('_', ' ') : null;
            }
            var model = Field("model") ?? Field("device") ?? Field("product") ?? "Phone";
            devices.Add(new ConnectedDevice(serial, model, state));
        }
        return devices;
    }

    /// <summary>
    /// Best-effort settings once ADB is authorized. Cannot enable USB
    /// debugging itself (Android security); optimizes an already-authorized session.
    /// </summary>
    public void TryOptimizeDevice(string serial)
    {
        try
        {
            // Keep screen on while charging — shop transfers don't sleep mid-push
            Run($"-s {serial} shell settings put global stay_on_while_plugged_in 3");
        }
        catch { }
        try
        {
            // Confirm adb stays enabled for this session
            Run($"-s {serial} shell settings put global adb_enabled 1");
        }
        catch { }
    }

    public byte[] GetHardwareFingerprint(string deviceSerial)
    {
        // MUST match DeviceFingerprint.kt: SHA-256(androidId + "|" + abi)
        // Serial is not used — the app cannot read ro.serialno on modern Android.
        var androidId = NormalizeId(Run($"-s {deviceSerial} shell settings get secure android_id"));
        var abi = NormalizeId(Run($"-s {deviceSerial} shell getprop ro.product.cpu.abi"));
        var payload = Encoding.UTF8.GetBytes($"{androidId}|{abi}");
        return System.Security.Cryptography.SHA256.HashData(payload);
    }

    private static string NormalizeId(string raw)
    {
        var s = (raw ?? "").Trim().Trim('\r', '\n');
        if (string.IsNullOrWhiteSpace(s) ||
            s.Equals("null", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            return "";
        return s;
    }

    public void PushToDevice(string serial, string localPath, string remoteFileName)
    {
        var dirs = new[]
        {
            "/sdcard/Android/data/com.shammapps.xama/files/incoming",
            "/sdcard/Download/XamaIncoming",
            "/storage/emulated/0/Download/XamaIncoming",
        };
        Exception? last = null;
        var ok = false;
        foreach (var remoteDir in dirs)
        {
            try
            {
                Run($"-s {serial} shell mkdir -p \"{remoteDir}\"");
                Run($"-s {serial} push \"{localPath}\" \"{remoteDir}/{remoteFileName}\"");
                ok = true;
            }
            catch (Exception ex) { last = ex; }
        }
        if (!ok) throw last ?? new InvalidOperationException("Push failed on every path.");
    }

    public void PushPlainToDevice(string serial, string localPath, string remoteFileName)
    {
        var remoteDir = "/sdcard/Android/data/com.shammapps.xama/files/plain";
        Run($"-s {serial} shell mkdir -p \"{remoteDir}\"");
        // Also drop a copy into public Movies for any player
        var publicDir = "/sdcard/Movies/Xama";
        Run($"-s {serial} shell mkdir -p \"{publicDir}\"");
        Run($"-s {serial} push \"{localPath}\" \"{remoteDir}/{remoteFileName}\"");
        try
        {
            Run($"-s {serial} push \"{localPath}\" \"{publicDir}/{remoteFileName}\"");
        }
        catch { /* public path may be restricted on some OEMs */ }
    }

    public string Run(string args) => Run(args, 30 * 60_000);

    /// <summary>
    /// Run adb with event-based output capture and a hard timeout. Never blocks on
    /// pipe EOF (which a forked adb daemon may hold open).
    /// </summary>
    public string Run(string args, int timeoutMs)
    {
        if (_adbPath == null) throw new InvalidOperationException("adb.exe not found.");
        var psi = new ProcessStartInfo
        {
            FileName = _adbPath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var outDone = new ManualResetEventSlim();
        using var errDone = new ManualResetEventSlim();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data == null) outDone.Set(); else lock (stdout) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data == null) errDone.Set(); else lock (stderr) stderr.AppendLine(e.Data); };
        if (!p.Start()) throw new InvalidOperationException("Could not start adb.");
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch { }
            Log($"TIMEOUT adb {args}");
            throw new TimeoutException($"adb {args} timed out");
        }
        // Drain whatever is buffered; don't wait forever for EOF.
        outDone.Wait(2000);
        errDone.Wait(500);
        string so, se;
        lock (stdout) so = stdout.ToString();
        lock (stderr) se = stderr.ToString();
        if (p.ExitCode != 0 && string.IsNullOrWhiteSpace(so))
        {
            Log($"adb {args} exit={p.ExitCode}: {se.Trim()}");
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(se) ? $"adb failed: {args}" : se.Trim());
        }
        return so;
    }

    public void Dispose() => StopWatching();
}
