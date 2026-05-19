using System.Windows;
using Microsoft.Win32;

namespace NetClipboard;

public partial class App : Application
{
    /// <summary>
    /// Window.Closing distinguishes user-initiated X (cancel + hide to tray)
    /// from a real exit (tray menu, Updater, OS session end) by checking this.
    /// </summary>
    public static bool IsShuttingDown { get; set; }

    public static void RequestExit()
    {
        IsShuttingDown = true;
        Current.Shutdown();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ThemeManager.Apply(AppTheme.System);
        ThemeManager.StartWatcher();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        IsShuttingDown = true;
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ThemeManager.StopWatcher();
        base.OnExit(e);
    }
}
