using System.Diagnostics;
using System.IO;

namespace XSeller.Services;

/// <summary>
/// Installs the bundled ADB USB drivers (Google USB Driver + ClockworkMod Universal ADB
/// Driver) via drivers\install-drivers.cmd with UAC elevation. Install-XSeller.bat does
/// the same at install time; the app re-offers it when a phone shows a driver problem.
/// </summary>
public static class DriverInstaller
{
    public static string DriversDir =>
        Path.Combine(Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? AppContext.BaseDirectory, "drivers");

    public static string ScriptPath => Path.Combine(DriversDir, "install-drivers.cmd");

    /// <summary>Written by install-drivers.cmd after a successful run.</summary>
    public static string InstalledMarker => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "XSeller", "drivers-installed.txt");

    private static string AttemptMarker => Path.Combine(FirstRunBootstrap.DocsRoot, "drivers-attempted.txt");

    public static bool Bundled => File.Exists(ScriptPath);
    public static bool Installed => File.Exists(InstalledMarker);
    public static bool AttemptedBefore => File.Exists(AttemptMarker);

    /// <summary>Run the elevated installer and wait. Returns false if declined/failed.</summary>
    public static bool InstallElevated()
    {
        if (!Bundled)
        {
            AdbService.Log("driver install: script missing at " + ScriptPath);
            return false;
        }
        try
        {
            Directory.CreateDirectory(FirstRunBootstrap.DocsRoot);
            File.WriteAllText(AttemptMarker, DateTime.Now.ToString("O"));
            var psi = new ProcessStartInfo(ScriptPath, "/quiet")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = DriversDir,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(5 * 60_000);
            AdbService.Log($"driver install exit={(p.HasExited ? p.ExitCode : -1)} installed={Installed}");
            return Installed;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            AdbService.Log("driver install: UAC declined");
            return false;
        }
        catch (Exception ex)
        {
            AdbService.Log("driver install failed: " + ex.Message);
            return false;
        }
    }
}
