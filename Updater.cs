using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace NetClipboard;

public record UpdateInfo(Version Current, Version Latest, string DownloadUrl, string ReleaseUrl);

/// <summary>
/// Checks the GitHub Releases API, downloads a new binary into the install
/// directory, and swaps it in by renaming the running exe to .old. Windows
/// allows MOVE on a running exe (rename, same volume), so the new binary
/// can take the original path while the old process is still alive.
/// </summary>
public static class Updater
{
    private const string Owner = "AndrewMoryakov";
    private const string Repo = "NetClipboard";
    private const string AssetName = "NetClipboard.exe";

    // Single client; per-call timeouts via CancellationToken so big downloads
    // aren't bound by HttpClient.Timeout.
    private static readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

    static Updater()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NetClipboard-Updater/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    /// <summary>Returns null when the API can't be reached or the latest is not newer.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var apiCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        apiCts.CancelAfter(TimeSpan.FromSeconds(10));

        var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
        var json = await _http.GetStringAsync(url, apiCts.Token);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tag = (root.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
        if (!Version.TryParse(tag, out var latest)) return null;

        var current = CurrentVersion;
        if (latest <= current) return null;

        string? exeUrl = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase))
            {
                exeUrl = asset.GetProperty("browser_download_url").GetString();
                break;
            }
        }
        if (string.IsNullOrEmpty(exeUrl)) return null;

        var releaseUrl = root.GetProperty("html_url").GetString() ?? "";
        return new UpdateInfo(current, latest, exeUrl, releaseUrl);
    }

    public static async Task<string> DownloadAsync(
        string url,
        IProgress<(long downloaded, long total)>? progress = null,
        CancellationToken ct = default)
    {
        // Download next to the running exe so the final Move is a same-volume rename.
        var currentExe = CurrentExePath();
        var dir = Path.GetDirectoryName(currentExe)!;
        var tempPath = Path.Combine(dir, $"NetClipboard_update_{Guid.NewGuid():N}.exe");

        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(tempPath))
        {
            var buf = new byte[64 * 1024];
            long downloaded = 0;
            var lastReport = DateTime.MinValue;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                downloaded += n;
                var now = DateTime.UtcNow;
                if (progress != null && (now - lastReport).TotalMilliseconds > 200)
                {
                    progress.Report((downloaded, total));
                    lastReport = now;
                }
            }
            progress?.Report((downloaded, total));
        }
        return tempPath;
    }

    /// <summary>
    /// Rename running exe to .old, move downloaded into its place, launch new
    /// process, shut WPF down. The .old file is deleted on next startup
    /// via CleanupOldVersion().
    /// </summary>
    public static void ApplyAndRestart(string newExePath)
    {
        var currentExe = CurrentExePath();
        var oldExe = currentExe + ".old";

        try { File.Delete(oldExe); } catch { }
        File.Move(currentExe, oldExe);
        File.Move(newExePath, currentExe);
        Process.Start(new ProcessStartInfo { FileName = currentExe, UseShellExecute = true });
        Application.Current.Shutdown();
    }

    public static void CleanupOldVersion()
    {
        try
        {
            var oldExe = CurrentExePath() + ".old";
            if (File.Exists(oldExe)) File.Delete(oldExe);
        }
        catch { }
    }

    private static string CurrentExePath()
    {
        var path = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(path))
            throw new InvalidOperationException("Cannot determine current executable path");
        return path;
    }
}
