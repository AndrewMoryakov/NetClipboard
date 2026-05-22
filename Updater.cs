using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace NetClipboard;

public record UpdateInfo(
    Version Current,
    Version Latest,
    string DownloadUrl,
    string Sha256Url,
    string ReleaseUrl);

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
    private const string Sha256AssetName = "NetClipboard.exe.sha256";

    public static string RepositoryUrl => $"https://github.com/{Owner}/{Repo}";
    public static string ReleasesUrl => $"{RepositoryUrl}/releases";

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
        return ParseReleaseJson(json, CurrentVersion);
    }

    internal static UpdateInfo? ParseReleaseJson(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tag = (root.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
        if (!Version.TryParse(tag, out var latest)) return null;

        if (latest <= current) return null;

        string? exeUrl = null;
        string? sha256Url = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase))
            {
                exeUrl = asset.GetProperty("browser_download_url").GetString();
            }
            else if (string.Equals(name, Sha256AssetName, StringComparison.OrdinalIgnoreCase))
            {
                sha256Url = asset.GetProperty("browser_download_url").GetString();
            }
        }
        if (!IsTrustedReleaseAssetUrl(exeUrl) || !IsTrustedReleaseAssetUrl(sha256Url))
            return null;

        var releaseUrl = root.GetProperty("html_url").GetString() ?? "";
        if (!IsTrustedGitHubUrl(releaseUrl)) return null;

        return new UpdateInfo(current, latest, exeUrl!, sha256Url!, releaseUrl);
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

        try
        {
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
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public static async Task<string> DownloadAndVerifyAsync(
        UpdateInfo info,
        IProgress<(long downloaded, long total)>? progress = null,
        CancellationToken ct = default)
    {
        var downloaded = await DownloadAsync(info.DownloadUrl, progress, ct);
        try
        {
            var manifest = await _http.GetStringAsync(info.Sha256Url, ct);
            var expectedHash = ParseSha256Manifest(manifest);
            var actualHash = await ComputeSha256Async(downloaded, ct);

            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Downloaded update hash does not match the release manifest.");

            return downloaded;
        }
        catch
        {
            TryDelete(downloaded);
            throw;
        }
    }

    public static bool CanWriteInstallDirectory(out string? error)
    {
        try
        {
            var dir = Path.GetDirectoryName(CurrentExePath())!;
            var probe = Path.Combine(dir, $".netclipboard_write_probe_{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
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
        var currentMoved = false;

        TryDelete(oldExe);
        try
        {
            File.Move(currentExe, oldExe);
            currentMoved = true;
            File.Move(newExePath, currentExe);
            Process.Start(new ProcessStartInfo { FileName = currentExe, UseShellExecute = true });
            // Mark as real exit so MainWindow.OnClosing doesn't trap us in the tray.
            App.IsShuttingDown = true;
            Application.Current.Shutdown();
        }
        catch
        {
            if (currentMoved)
                TryRollback(currentExe, oldExe);
            throw;
        }
    }

    public static void CleanupOldVersion()
    {
        try
        {
            var oldExe = CurrentExePath() + ".old";
            if (File.Exists(oldExe)) File.Delete(oldExe);
        }
        catch { }

        try
        {
            var dir = Path.GetDirectoryName(CurrentExePath())!;
            foreach (var temp in Directory.EnumerateFiles(dir, "NetClipboard_update_*.exe"))
                TryDelete(temp);
        }
        catch { }
    }

    internal static string ParseSha256Manifest(string text)
    {
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var token = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (token == null) continue;
            if (IsSha256Hex(token)) return token.ToLowerInvariant();
        }

        throw new InvalidDataException("Release hash manifest does not contain a valid SHA-256 hash.");
    }

    internal static bool IsSha256Hex(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    internal static async Task<string> ComputeSha256Async(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.Create().ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsTrustedReleaseAssetUrl(string? value)
    {
        if (!IsTrustedGitHubUrl(value)) return false;
        var uri = new Uri(value!);
        return uri.AbsolutePath.Contains($"/{Owner}/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTrustedGitHubUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryRollback(string currentExe, string oldExe)
    {
        try
        {
            if (File.Exists(currentExe))
                File.Delete(currentExe);
            if (File.Exists(oldExe))
                File.Move(oldExe, currentExe);
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
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
