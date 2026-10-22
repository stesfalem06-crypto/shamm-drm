using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace XSeller.Services;

/// <summary>
/// Offline update: looks for a newer XSeller-Setup-*.exe next to the app, in Documents\XSeller\updates,
/// or on any removable/fixed drive under \XSeller-updates\ or the drive root. Never downloads.
/// </summary>
public static class UpdateChecker
{
    public static Version Current => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    public sealed record Offer(string Path, Version Version);

    public static Offer? FindNewer()
    {
        Offer? best = null;
        foreach (var dir in SearchDirs())
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "XSeller-Setup*.exe"))
            {
                var v = VersionOf(f);
                if (v == null || v <= Current) continue;
                if (best == null || v > best.Version) best = new Offer(f, v);
            }
        }
        return best;
    }

    private static List<string> SearchDirs()
    {
        var list = new List<string> { Path.Combine(FirstRunBootstrap.DocsRoot, "updates") };
        try
        {
            var d = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrEmpty(d)) list.Add(d);
        }
        catch { }
        try { list.Add(AppContext.BaseDirectory.TrimEnd('\\', '/')); } catch { }
        try
        {
            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && (d.DriveType is DriveType.Removable or DriveType.Fixed)))
            {
                list.Add(Path.Combine(d.Name, "XSeller-updates"));
                list.Add(d.Name.TrimEnd('\\'));
            }
        }
        catch { }
        return list;
    }

    private static Version? VersionOf(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (Version.TryParse(info.FileVersion, out var v)) return v;
            var name = Path.GetFileNameWithoutExtension(path);
            foreach (var p in name.Split('-').Reverse())
                if (Version.TryParse(p, out v)) return v;
        }
        catch { }
        return null;
    }

    public static void OfferIfNewer(System.Windows.Window? owner)
    {
        try
        {
            var o = FindNewer();
            if (o == null) return;
            var ans = System.Windows.MessageBox.Show(owner,
                $"A newer X Seller installer was found:\n{o.Path}\n\nVersion {o.Version} (you have {Current}).\n\nInstall it now? Your sales data stays on this PC.",
                "X Seller update", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Information);
            if (ans != System.Windows.MessageBoxResult.Yes) return;
            Process.Start(new ProcessStartInfo(o.Path) { UseShellExecute = true });
        }
        catch (Exception ex) { AdbService.Log("update check: " + ex.Message); }
    }
}
