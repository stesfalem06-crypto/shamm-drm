using System.Windows;
using XamaMaster.Services;

namespace XamaMaster;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        try { FirstRunBootstrap.Run(); } catch { }
        base.OnStartup(e);
    }
}
