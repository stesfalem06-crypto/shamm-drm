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
        base.OnStartup(e);
    }
}
