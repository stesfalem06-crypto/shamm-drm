using System.Diagnostics;
using System.IO;
using System.Text;

namespace XSeller.Services;

public record ConnectedDevice(string Serial, string Model, string State);

/// <summary>
/// ADB bridge. Continuously watches for phones; once the user taps "Allow"
/// on the RSA prompt we auto-apply shop-friendly settings.
///
/// Note: Android does NOT allow a PC to turn on USB debugging the first time
/// without Developer options — that is a platform security rule. After the
/// user enables it once and accepts this computer, we keep the session
/// optimized automatically.
/// </summary>
public class AdbService : IDisposable
{
    private readonly string _adbPath;
    private readonly object _gate = new();
    private CancellationTokenSource? _watchCts;
    private List<ConnectedDevice> _last = new();

    public event Action<List<ConnectedDevice>>? DevicesChanged;

    public AdbService()
    {
        _adbPath = FirstRunBootstrap.ResolveAdb() ?? "adb";
    }

    public void StartServer()
    {
        try { Run("start-server"); } catch { /* adb missing */ }
    }

    public void StartWatching(int intervalMs = 1500)
    {
        StopWatching();
        _watchCts = new CancellationTokenSource();
        var token = _watchCts.Token;
        Task.Run(async () =>
        {
            StartServer();
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var devices = ListDevices();
                    var changed = devices.Count != _last.Count ||
                                  devices.Select(d => d.Serial + d.State)
                                      .Except(_last.Select(d => d.Serial + d.State)).Any();
                    if (changed)
                    {
                        var newlyAuthorized = devices
                            .Where(d => d.State == "device")
                            .Where(d => _last.All(x => x.Serial != d.Serial) ||
                                        _last.Any(x => x.Serial == d.Serial && x.State != "device"))
                            .ToList();
                        _last = devices;
                        DevicesChanged?.Invoke(devices);
                        foreach (var d in newlyAuthorized)
                            TryOptimizeDevice(d.Serial);
                    }
                }
                catch { /* transient adb */ }
                try { await Task.Delay(intervalMs, token); } catch { break; }
            }
        }, token);
    }

    public void StopWatching()
    {
        try { _watchCts?.Cancel(); } catch { }
        _watchCts = null;
    }

    public List<ConnectedDevice> ListDevices()
    {
        var devices = new List<ConnectedDevice>();
        string output;
        try { output = Run("devices -l"); }
        catch { return devices; }

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var serial = parts[0];
            var state = parts[1]; // device | unauthorized | offline | recovery
            var model = "Phone";
            var modelIdx = trimmed.IndexOf("model:", StringComparison.Ordinal);
            if (modelIdx >= 0)
            {
                var rest = trimmed[(modelIdx + 6)..];
                model = rest.Split(' ')[0].Replace('_', ' ');
            }
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
        var androidId = Run($"-s {deviceSerial} shell settings get secure android_id").Trim();
        var serial = Run($"-s {deviceSerial} shell getprop ro.serialno").Trim();
        var abi = Run($"-s {deviceSerial} shell getprop ro.product.cpu.abi").Trim();
        if (string.IsNullOrWhiteSpace(androidId)) androidId = "unknown";
        if (string.IsNullOrWhiteSpace(serial)) serial = deviceSerial;
        if (string.IsNullOrWhiteSpace(abi)) abi = "armeabi-v7a";
        var payload = Encoding.UTF8.GetBytes($"{androidId}|{serial}|{abi}");
        return System.Security.Cryptography.SHA256.HashData(payload);
    }

    public void PushToDevice(string serial, string localPath, string remoteFileName)
    {
        // App-private incoming folder used by Xama
        var remoteDir = "/sdcard/Android/data/com.shammapps.xama/files/incoming";
        Run($"-s {serial} shell mkdir -p \"{remoteDir}\"");
        var remote = $"{remoteDir}/{remoteFileName}";
        Run($"-s {serial} push \"{localPath}\" \"{remote}\"");
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

    public string Run(string args)
    {
        lock (_gate)
        {
            var psi = new ProcessStartInfo
            {
                FileName = _adbPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start adb.");
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(120_000);
            if (p.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout))
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? $"adb failed: {args}" : stderr.Trim());
            return stdout;
        }
    }

    public void Dispose() => StopWatching();
}
