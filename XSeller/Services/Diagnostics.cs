using System.Diagnostics;
using System.IO;
using System.Text;

namespace XSeller.Services;

/// <summary>Collects everything needed to debug "phone not detected" into one text blob.</summary>
public static class Diagnostics
{
    public static string DiagPath => Path.Combine(FirstRunBootstrap.DocsRoot, "diag.txt");

    public static string Collect(AdbService adb, MtpService? mtp = null)
    {
        var sb = new StringBuilder();
        void H(string t) => sb.AppendLine().AppendLine("===== " + t + " =====");
        string Try(Func<string> f) { try { return f(); } catch (Exception ex) { return "ERROR: " + ex.Message; } }

        H("X Seller");
        sb.AppendLine($"Time: {DateTime.Now:O}");
        sb.AppendLine($"Version: {typeof(Diagnostics).Assembly.GetName().Version}");
        sb.AppendLine($"OS: {Environment.OSVersion} 64bit={Environment.Is64BitOperatingSystem}");
        sb.AppendLine($"ProcessPath: {Environment.ProcessPath}");
        sb.AppendLine($"BaseDirectory: {AppContext.BaseDirectory}");
        sb.AppendLine($"adb resolved: {adb.AdbPath ?? "(none)"}");
        sb.AppendLine($"adb status: {adb.LastStatus}");
        sb.AppendLine($"Drivers bundled: {DriverInstaller.Bundled} ({DriverInstaller.DriversDir}) installed: {DriverInstaller.Installed}");

        H("Database backups (encrypted, multi-location)");
        sb.AppendLine(Try(BackupService.DiagnosticsText));

        H("MTP / Windows Portable Devices (primary path)");
        sb.AppendLine(mtp == null ? "(n/a)" : Try(mtp.DiagnosticsText));

        H("adb candidates");
        foreach (var c in AdbService.AdbCandidates().Distinct())
            sb.AppendLine($"{(File.Exists(c) ? "FOUND  " : "missing")} {c}");

        H("adb processes");
        sb.AppendLine(Try(() =>
        {
            var lines = new StringBuilder();
            foreach (var p in Process.GetProcessesByName("adb"))
                using (p)
                {
                    string path; try { path = p.MainModule?.FileName ?? "?"; } catch { path = "(access denied)"; }
                    lines.AppendLine($"pid={p.Id} {path}");
                }
            return lines.Length == 0 ? "(none)" : lines.ToString();
        }));

        H("adb version");
        sb.AppendLine(Try(() => adb.Run("version", 15_000)));
        H("adb devices -l");
        sb.AppendLine(Try(() => adb.Run("devices -l", 15_000)));

        H("Phones seen by Windows (grouped)");
        var ents = UsbPhoneDetector.QueryUsbEntities();
        foreach (var ph in UsbPhoneDetector.Group(ents))
            sb.AppendLine($"{ph.State,-15} {ph.Name}  serial={ph.Serial ?? "?"} {ph.VidPid}");
        H("USB / WPD PnP devices");
        sb.AppendLine(UsbPhoneDetector.Dump(ents));

        H("driver-install.log");
        sb.AppendLine(Try(() => Tail(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "XSeller", "driver-install.log"), 80)));

        H("adb.log (last 300 lines)");
        sb.AppendLine(Try(() => Tail(AdbService.LogPath, 300)));
        return sb.ToString();
    }

    private static string Tail(string path, int lines)
    {
        if (!File.Exists(path)) return "(no file)";
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        var all = sr.ReadToEnd().Split('\n');
        return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)));
    }
}
