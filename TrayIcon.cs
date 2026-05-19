using System.Windows;
using WinForms = System.Windows.Forms;
using SD = System.Drawing;

namespace NetClipboard;

/// <summary>
/// Hosts a System.Windows.Forms.NotifyIcon for the WPF main window.
/// Owns the icon's lifetime and the tray context menu.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly Window _owner;

    public TrayIcon(Window owner)
    {
        _owner = owner;
        _icon = new WinForms.NotifyIcon
        {
            Icon = SD.SystemIcons.Application,
            Text = "NetClipboard",
            Visible = true
        };

        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) Restore();
        };
        _icon.DoubleClick += (_, _) => Restore();

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Show NetClipboard", null, (_, _) => Restore());
        menu.Items.Add("-");
        menu.Items.Add("Exit", null, (_, _) => App.RequestExit());
        _icon.ContextMenuStrip = menu;
    }

    public void ShowBalloon(string title, string text)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.ShowBalloonTip(3000);
    }

    private void Restore()
    {
        _owner.Show();
        if (_owner.WindowState == WindowState.Minimized)
            _owner.WindowState = WindowState.Normal;
        _owner.Activate();
        // Brief Topmost flicker to actually bring the window above others
        // when Windows is being grumpy about focus stealing.
        _owner.Topmost = true;
        _owner.Topmost = false;
        _owner.Focus();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
