using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Navigation;

namespace NetClipboard;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {Updater.CurrentVersion}";
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesBtn.IsEnabled = false;
        UpdateStatusText.Text = "Checking for updates...";

        try
        {
            var info = await Updater.CheckAsync();
            UpdateStatusText.Text = info == null
                ? "You're using the latest verified version."
                : $"Version {info.Latest} is available. Open releases to download or install it.";
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Couldn't check for updates. {ex.Message}";
        }
        finally
        {
            CheckUpdatesBtn.IsEnabled = true;
        }
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
