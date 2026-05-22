using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Navigation;

namespace NetClipboard;

public partial class AboutWindow : Window
{
    private UpdateInfo? _availableUpdate;

    public AboutWindow(UpdateInfo? availableUpdate = null)
    {
        InitializeComponent();
        VersionText.Text = $"Version {Updater.CurrentVersion}";
        SetAvailableUpdate(availableUpdate);
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        UpdateStatusText.Text = "Checking for updates...";

        try
        {
            var info = await Updater.CheckAsync();
            SetAvailableUpdate(info);
            if (info == null)
                UpdateStatusText.Text = "You're using the latest verified version.";
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Couldn't check for updates. {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate == null) return;

        if (!Updater.CanWriteInstallDirectory(out var writeError))
        {
            UpdateStatusText.Text = "Automatic install is unavailable. Open releases to install manually.";
            MessageBox.Show(this,
                "NetClipboard cannot write to its install directory, so automatic install is not available.\n\n" +
                $"Release: {_availableUpdate.ReleaseUrl}\n\n" +
                $"Details: {writeError}",
                "NetClipboard update",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        SetBusy(true);
        UpdateStatusText.Text = "Downloading and verifying update...";
        var progress = new Progress<(long d, long t)>(p =>
        {
            var mb = p.d / 1024 / 1024;
            UpdateStatusText.Text = p.t > 0
                ? $"Downloading update: {(int)(p.d * 100 / p.t)}% ({mb} / {p.t / 1024 / 1024} MB)"
                : $"Downloading update: {mb} MB";
        });

        string downloaded;
        try
        {
            using var downloadCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            downloaded = await Updater.DownloadAndVerifyAsync(_availableUpdate, progress, downloadCts.Token);
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText.Text = "Update download timed out. Try again.";
            SetBusy(false);
            return;
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Update download or verification failed. {ex.Message}";
            SetBusy(false);
            return;
        }

        UpdateStatusText.Text = "Installing update. NetClipboard will restart.";
        await Task.Delay(800);
        try
        {
            Updater.ApplyAndRestart(downloaded);
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Update install failed. {ex.Message}";
            SetBusy(false);
        }
    }

    private void SetAvailableUpdate(UpdateInfo? info)
    {
        _availableUpdate = info;
        if (info == null)
        {
            AvailableVersionText.Text = "No update available.";
            InstallUpdateBtn.IsEnabled = false;
            return;
        }

        AvailableVersionText.Text = $"Version {info.Latest} is available.";
        UpdateStatusText.Text = "Install the verified update from this window, or open releases to install manually.";
        InstallUpdateBtn.IsEnabled = true;
    }

    private void SetBusy(bool busy)
    {
        CheckUpdatesBtn.IsEnabled = !busy;
        InstallUpdateBtn.IsEnabled = !busy && _availableUpdate != null;
    }

    private void RepositoryLink_Click(object sender, RoutedEventArgs e) =>
        OpenUrl(Updater.RepositoryUrl);

    private void Releases_Click(object sender, RoutedEventArgs e) =>
        OpenUrl(Updater.ReleasesUrl);

    private void Close_Click(object sender, RoutedEventArgs e) =>
        Close();

    private static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }
}
