using System.Windows;
using XSeller.Services;

namespace XSeller;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Install once → use: no wizard, no extra steps
        try { FirstRunBootstrap.Run(); }
        catch { /* never block launch */ }
        // Encrypted DB backups: restore a newer/healthier copy before anything opens the main DB.
        try { BackupService.StartupRestore(); } catch { }
        // Weak GPU (tier 0), remote desktop or broken drivers: software rendering is smoother and avoids glitches.
        try
        {
            var tier = System.Windows.Media.RenderCapability.Tier >> 16;
            if (tier == 0 || SystemParameters.IsRemoteSession ||
                Environment.GetEnvironmentVariable("XSELLER_SOFTWARE_RENDER") == "1")
                System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        }
        catch { }
        base.OnStartup(e);
    }
}
