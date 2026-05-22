using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Windows;
using Microsoft.Win32;

namespace NetClipboard;

public partial class App : Application
{
    private const string SingleInstanceMutexName = "Local\\NetClipboard.SingleInstance";
    private const string ActivationPipeName = "NetClipboard.Activate";

    private Mutex? _singleInstanceMutex;
    private CancellationTokenSource? _activationCts;
    private bool _ownsSingleInstanceMutex;

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

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var ownsMutex);
        if (!ownsMutex)
        {
            SignalExistingInstance();
            Shutdown();
            return;
        }
        _ownsSingleInstanceMutex = true;

        ThemeManager.Apply(AppTheme.System);
        ThemeManager.StartWatcher();
        StartActivationListener();

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        IsShuttingDown = true;
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationCts?.Cancel();
        _activationCts?.Dispose();
        if (_ownsSingleInstanceMutex)
            _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        ThemeManager.StopWatcher();
        base.OnExit(e);
    }

    private void StartActivationListener()
    {
        _activationCts = new CancellationTokenSource();
        _ = Task.Run(() => ActivationListenLoop(_activationCts.Token));
    }

    private async Task ActivationListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    ActivationPipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(pipe);
                _ = await reader.ReadLineAsync(ct);
                _ = Dispatcher.BeginInvoke(ActivateMainWindow);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                try { await Task.Delay(250, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", ActivationPipeName, PipeDirection.Out);
            pipe.Connect(750);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            writer.WriteLine("show");
        }
        catch { }
    }

    private void ActivateMainWindow()
    {
        if (MainWindow == null) return;

        MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized)
            MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
        MainWindow.Topmost = true;
        MainWindow.Topmost = false;
        MainWindow.Focus();
    }
}
