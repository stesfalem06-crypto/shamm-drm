using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace XSeller.Services;

/// <summary>
/// Zero-config first launch: create folders, ensure ADB server is up,
/// write defaults. Shop owner only installs/runs the exe — no manual setup.
/// </summary>
public static class FirstRunBootstrap
{
    public static string DocsRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "XSeller");

    public static string LibraryDir => Path.Combine(DocsRoot, "Library");
    public static string PlainDir => Path.Combine(DocsRoot, "Plain");
    public static string MarkerPath => Path.Combine(DocsRoot, ".ready");

    public static void Run()
    {
        Directory.CreateDirectory(LibraryDir);
        Directory.CreateDirectory(PlainDir);
        Directory.CreateDirectory(DocsRoot);

        // Shop name default once
        var shopPath = Path.Combine(DocsRoot, "shopname.txt");
        if (!File.Exists(shopPath))
            File.WriteAllText(shopPath, Environment.MachineName);

        // Drop a short "put files here" note only once (not a setup wizard)
        var readmeLib = Path.Combine(LibraryDir, "PUT_PROTECTED_PACKAGES_HERE.txt");
        if (!File.Exists(readmeLib))
            File.WriteAllText(readmeLib,
                "Copy .shammvid + .shammmeta packages from Xama Master into this folder.\n" +
                "X Seller indexes them automatically.\n");

        var readmePlain = Path.Combine(PlainDir, "PUT_OPEN_VIDEOS_HERE.txt");
        if (!File.Exists(readmePlain))
            File.WriteAllText(readmePlain,
                "Optional: put normal (non-encrypted) videos here for fast search + send.\n" +
                "Downloads and Videos folders are also indexed automatically.\n");

        // Start ADB server silently so the first phone plug works immediately
        try
        {
            var adb = ResolveAdb();
            if (adb != null)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = adb,
                    Arguments = "start-server",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(15_000);
            }
        }
        catch { /* offline ok */ }

        File.WriteAllText(MarkerPath, DateTime.UtcNow.ToString("O"));
    }

    public static string? ResolveAdb()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var bundled = Path.Combine(baseDir, "platform-tools", "adb.exe");
        if (File.Exists(bundled)) return bundled;
        // single-file sibling
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? baseDir;
        bundled = Path.Combine(exeDir, "platform-tools", "adb.exe");
        if (File.Exists(bundled)) return bundled;
        return null;
    }
}
