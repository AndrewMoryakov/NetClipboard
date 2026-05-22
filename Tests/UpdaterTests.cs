using System.IO;
using System.Text;
using NetClipboard;
using Xunit;

namespace NetClipboard.Tests;

public sealed class UpdaterTests
{
    private const string ValidHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData(ValidHash)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    [InlineData(ValidHash + "  NetClipboard.exe")]
    [InlineData("\r\n  " + ValidHash + "  NetClipboard.exe\r\n")]
    public void ParseSha256Manifest_accepts_common_formats(string manifest)
    {
        var parsed = Updater.ParseSha256Manifest(manifest);

        Assert.Equal(ValidHash, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("0123456789abcdef")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void ParseSha256Manifest_rejects_invalid_hashes(string manifest)
    {
        Assert.Throws<InvalidDataException>(() => Updater.ParseSha256Manifest(manifest));
    }

    [Fact]
    public async Task ComputeSha256Async_returns_lowercase_hash()
    {
        var path = Path.Combine(Path.GetTempPath(), $"netclipboard_hash_test_{Guid.NewGuid():N}.txt");
        await File.WriteAllBytesAsync(path, Encoding.ASCII.GetBytes("hello"));

        try
        {
            var hash = await Updater.ComputeSha256Async(path);

            Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", hash);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ParseReleaseJson_requires_sha256_asset()
    {
        var json = ReleaseJson(includeSha256: false);

        var info = Updater.ParseReleaseJson(json, new Version(1, 0, 0));

        Assert.Null(info);
    }

    [Fact]
    public void ParseReleaseJson_returns_update_when_exe_and_sha256_assets_exist()
    {
        var json = ReleaseJson(includeSha256: true);

        var info = Updater.ParseReleaseJson(json, new Version(1, 0, 0));

        Assert.NotNull(info);
        Assert.Equal(new Version(1, 0, 1), info.Latest);
        Assert.Equal("https://github.com/AndrewMoryakov/NetClipboard/releases/download/v1.0.1/NetClipboard.exe", info.DownloadUrl);
        Assert.Equal("https://github.com/AndrewMoryakov/NetClipboard/releases/download/v1.0.1/NetClipboard.exe.sha256", info.Sha256Url);
    }

    private static string ReleaseJson(bool includeSha256)
    {
        var shaAsset = includeSha256
            ? """
              ,
              {
                "name": "NetClipboard.exe.sha256",
                "browser_download_url": "https://github.com/AndrewMoryakov/NetClipboard/releases/download/v1.0.1/NetClipboard.exe.sha256"
              }
              """
            : "";

        return $$"""
          {
            "tag_name": "v1.0.1",
            "html_url": "https://github.com/AndrewMoryakov/NetClipboard/releases/tag/v1.0.1",
            "assets": [
              {
                "name": "NetClipboard.exe",
                "browser_download_url": "https://github.com/AndrewMoryakov/NetClipboard/releases/download/v1.0.1/NetClipboard.exe"
              }
              {{shaAsset}}
            ]
          }
          """;
    }
}
