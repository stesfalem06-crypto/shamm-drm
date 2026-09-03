using System.Diagnostics;
using System.IO;

namespace XSeller.Services;

public record ConnectedDevice(string Serial, string Model);

/// <summary>
/// Wraps the standard Android "adb" tool (bundled in platform-tools/ next
/// to XSeller.exe by the GitHub Actions build - see build-xseller.yml).
/// ADB is Google's own official USB debugging bridge; using it instead of
/// hand-rolling USB protocol code is both far more reliable and something
/// every Android phone already supports once USB debugging is turned on.
///
/// One thing the shop owner must do once per phone: enable "USB debugging"
/// in the phone's Developer Options and accept the one-time "Allow this
/// computer?" prompt. Xama's first-run screen (Phase 4) walks the end user
/// through this so it isn't a mystery.
/// </summary>
public class AdbService
{
    private readonly string _adbPath;

    public AdbService()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        _adbPath = Path.Combine(baseDir, "platform-tools", "adb.exe");
    }

    public List<ConnectedDevice> ListDevices()
    {
        var devices = new List<ConnectedDevice>();
        var output = Run("devices -l");
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || !trimmed.Contains("device")) continue;
            var serial = trimmed.Split(' ')[0];
            var model = "Unknown model";
            var modelIdx = trimmed.IndexOf("model:", StringComparison.Ordinal);
            if (modelIdx >= 0)
            {
                var rest = trimmed[(modelIdx + 6)..];
                model = rest.Split(' ')[0];
            }
            devices.Add(new ConnectedDevice(serial, model));
        }
        return devices;
    }

    /// <summary>
    /// Builds this phone's hardware fingerprint the exact same way Xama
    /// will rebuild it locally at playback time: SHA-256(Android ID +
    /// hardware serial + primary ABI). Must never drift from Xama's own
    /// copy of this logic (Android/app/.../DeviceFingerprint.kt).
    /// </summary>
    public byte[] GetHardwareFingerprint(string deviceSerial)
    {
        var androidId = Run($"-s {deviceSerial} shell settings get secure android_id").Trim();
        var serial = Run($"-s {deviceSerial} shell getprop ro.serialno").Trim();
        var abi = Run($"-s {deviceSerial} shell getprop ro.product.cpu.abi").Trim();

        var combined = $"{androidId}|{serial}|{abi}";
        using var sha = System.Security.Cryptography.SHA256.Create();
        return sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(combined));
    }

    /// <summary>
    /// Pushes a file into Xama's protected app-private storage on the
    /// phone (not the public Downloads/Movies folder), so the encrypted
    /// video and its wrapped key never sit somewhere a file manager or
    /// another app can casually copy them from.
    /// </summary>
    public void PushToDevice(string deviceSerial, string localPath, string remoteFileName)
    {
        const string remoteDir = "/sdcard/Android/data/com.shammapps.xama/files/incoming";
        Run($"-s {deviceSerial} shell mkdir -p {remoteDir}");
        Run($"-s {deviceSerial} push \"{localPath}\" \"{remoteDir}/{remoteFileName}\"");
    }

    private string Run(string args)
    {
        if (!File.Exists(_adbPath))
            throw new FileNotFoundException(
                "adb.exe not found next to XSeller.exe. This should be bundled automatically " +
                "by the build - if it's missing, re-download the latest X Seller build.", _adbPath);

        var psi = new ProcessStartInfo
        {
            FileName = _adbPath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(15000);
        return stdout;
    }
}
