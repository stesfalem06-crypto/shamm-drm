using System.Diagnostics;
using System.IO;

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

        var readmePlain = Path.Combine(PlainDir, "PUT_OPEN_MEDIA_HERE.txt");
        if (!File.Exists(readmePlain))
            File.WriteAllText(readmePlain,
                "Optional: put open (non-encrypted) media here for fast search + send.\n" +
                "Supported: video, PDF, images, and .exe. Downloads / Videos / Desktop are\n" +
                "also indexed by default (customize via Add / Remove media folder).\n");

        // The adb server is started by AdbService's watcher thread (without redirected
        // pipes, which would hang on the forked daemon). Nothing to do here, so launch
        // is never delayed by adb.

        File.WriteAllText(MarkerPath, DateTime.UtcNow.ToString("O"));
    }

    public static string? ResolveAdb() => AdbService.ResolveAdb();
}
