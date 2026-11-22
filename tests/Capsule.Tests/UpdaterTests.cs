using System.Text;
using System.Text.Json;
using Capsule.Core;

namespace Capsule.Tests;

public class UpdaterTests
{
    private const string Asset = "Capsule-win-x64.exe";
    private static readonly Version Current = new(1, 0, 0);

    private static byte[] Release(string tag = "v1.1.0", bool draft = false, bool prerelease = false, long size = 1000,
        string? exeUrl = null, string? sumsUrl = null, bool includeSums = true)
    {
        var assets = new List<object>
        {
            new { name = Asset, size, browser_download_url = exeUrl ?? ProjectInfo.DownloadPrefix + tag + "/" + Asset },
        };
        if (includeSums)
        {
            assets.Add(new { name = "SHA256SUMS.txt", size = 200, browser_download_url = sumsUrl ?? ProjectInfo.DownloadPrefix + tag + "/SHA256SUMS.txt" });
        }

        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { tag_name = tag, draft, prerelease, assets }));
    }

    [Fact]
    public void Newer_release_is_offered()
    {
        var update = Updater.ParseRelease(Release(), Current, Asset);

        Assert.NotNull(update);
        Assert.Equal(new Version(1, 1, 0), update!.Version);
        Assert.Equal(1000, update.AssetSize);
        Assert.StartsWith(ProjectInfo.DownloadPrefix + "v1.1.0/", update.AssetUrl.AbsoluteUri);
    }

    [Theory]
    [InlineData("v1.0.0")]
    [InlineData("v0.9.9")]
    public void Same_or_older_release_is_ignored(string tag) =>
        Assert.Null(Updater.ParseRelease(Release(tag), Current, Asset));

    [Theory]
    [InlineData("1.1.0")]
    [InlineData("v1.1")]
    [InlineData("v1.1.0-beta")]
    [InlineData("v1.1.0/../../evil")]
    [InlineData("v99999.0.0")]
    public void Odd_tags_are_ignored(string tag) =>
        Assert.Null(Updater.ParseRelease(Release(tag), Current, Asset));

    [Fact]
    public void Drafts_and_prereleases_are_ignored()
    {
        Assert.Null(Updater.ParseRelease(Release(draft: true), Current, Asset));
        Assert.Null(Updater.ParseRelease(Release(prerelease: true), Current, Asset));
    }

    [Theory]
    [InlineData("https://evil.example/Capsule-win-x64.exe")]
    [InlineData("http://github.com/ahmedot/capsule-hud/releases/download/v1.1.0/Capsule-win-x64.exe")]
    [InlineData("https://github.com/someone-else/capsule-hud/releases/download/v1.1.0/Capsule-win-x64.exe")]
    [InlineData("https://github.com/ahmedot/capsule-hud/releases/download/v1.0.0/Capsule-win-x64.exe")]
    public void Downloads_from_anywhere_else_are_refused(string url) =>
        Assert.Null(Updater.ParseRelease(Release(exeUrl: url), Current, Asset));

    [Fact]
    public void Release_without_checksums_is_refused() =>
        Assert.Null(Updater.ParseRelease(Release(includeSums: false), Current, Asset));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(Updater.MaxDownloadBytes + 1)]
    public void Bad_sizes_are_refused(long size) =>
        Assert.Null(Updater.ParseRelease(Release(size: size), Current, Asset));

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"tag_name\": 5}")]
    public void Malformed_metadata_is_ignored(string json) =>
        Assert.Null(Updater.ParseRelease(Encoding.UTF8.GetBytes(json), Current, Asset));

    [Fact]
    public void Checksum_is_found_for_the_right_file()
    {
        var hash = new string('a', 64);
        var other = new string('b', 64);
        var listing = $"{other}  Capsule-win-arm64.exe\r\n{hash.ToUpperInvariant()}  Capsule-win-x64.exe\n";

        Assert.Equal(hash, Updater.ParseChecksum(listing, Asset));
        Assert.Equal(other, Updater.ParseChecksum(listing, "Capsule-win-arm64.exe"));
        Assert.Null(Updater.ParseChecksum(listing, "Capsule.exe"));
    }

    [Theory]
    [InlineData("abc  Capsule-win-x64.exe")]
    [InlineData("  ")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz  Capsule-win-x64.exe")]
    public void Malformed_checksums_are_ignored(string listing) =>
        Assert.Null(Updater.ParseChecksum(listing, Asset));

    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v10.0.1", 10, 0, 1)]
    public void Versions_parse(string text, int major, int minor, int patch) =>
        Assert.Equal(new Version(major, minor, patch), Updater.ParseVersion(text));
}
