using System.Windows;
using XamaMaster.Services;

namespace XamaMaster;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        try { FirstRunBootstrap.Run(); } catch { }
                // Fail early with a clear message if the native crypto DLL is missing (common after a bad unzip).
        try { Services.NativeCrypto.EnsureLoaded(); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Shamm Master — crypto library missing", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }
        base.OnStartup(e);
    }
}
