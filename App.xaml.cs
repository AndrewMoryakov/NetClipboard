using System.Windows;
using Microsoft.Win32;

namespace NetClipboard;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ThemeManager.Apply(AppTheme.System);
        ThemeManager.StartWatcher();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ThemeManager.StopWatcher();
        base.OnExit(e);
    }
}
