using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Capsule.Core;

internal sealed record UpdateInfo(Version Version, string Tag, string AssetName, Uri AssetUrl, long AssetSize, Uri ChecksumsUrl, Uri PageUrl);

/// <summary>
/// Checks GitHub Releases of this project for a newer version and installs it when the user agrees.
///
/// Safety rules:
/// - Talks only to api.github.com and this repository's release downloads, over HTTPS.
/// - Accepts only final (non-draft, non-prerelease) releases tagged vX.Y.Z.
/// - Download links must be exactly this repository's release URL for that tag and file name.
/// - The download must match the announced size and the SHA-256 in the release's SHA256SUMS.txt.
/// - Nothing is installed without the user saying yes.
/// </summary>
internal static class Updater
{
    public const string UpdatedArgument = "--updated";
    private const string ChecksumsName = "SHA256SUMS.txt";
    private const long MaxMetadataBytes = 1024 * 1024;
    private const long MaxChecksumBytes = 64 * 1024;
    public const long MaxDownloadBytes = 400L * 1024 * 1024;

    private static readonly Regex TagPattern = new(@"^v([0-9]{1,4})\.([0-9]{1,4})\.([0-9]{1,4})\z", RegexOptions.CultureInvariant);
    private static readonly Regex ChecksumLine = new(@"^([0-9A-Fa-f]{64})\s+\*?(\S+)$", RegexOptions.CultureInvariant);
    private static readonly HttpClient Http = CreateClient();

    public static string UpdatesFolder { get; } = Path.Combine(Log.Folder, "updates");

    public static string AssetName =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "Capsule-win-arm64.exe" : "Capsule-win-x64.exe";

    /// <summary>Null when this build's version can't be read: then no update is offered (avoids downgrades).</summary>
    public static Version? CurrentVersion => ParseVersion(App.Version);

    /// <summary>Returns the newer release, or null when up to date (or no release exists yet).</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken cancel)
    {
        if (!ProjectInfo.IsConfigured || CurrentVersion is null) return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, ProjectInfo.LatestReleaseApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        var json = await ReadLimitedAsync(response, MaxMetadataBytes, cancel);
        return CurrentVersion is { } current ? ParseRelease(json, current, AssetName) : null;
    }

    /// <summary>Pure parsing and validation of the GitHub "latest release" JSON.</summary>
    internal static UpdateInfo? ParseRelease(byte[] json, Version current, string assetName)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var release = document.RootElement;
            if (release.ValueKind != JsonValueKind.Object) return null;
            if (IsTrue(release, "draft") || IsTrue(release, "prerelease")) return null;

            // Release tags must be exactly vX.Y.Z (no leniency here, unlike the app's own version string).
            var tag = Text(release, "tag_name");
            if (tag is null || !TagPattern.IsMatch(tag)) return null;
            var version = ParseVersion(tag);
            if (version is null || version <= current) return null;

            Uri? exe = null, sums = null;
            long size = 0;
            if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.ValueKind != JsonValueKind.Object) continue;
                    var name = Text(asset, "name");
                    var url = Text(asset, "browser_download_url");
                    if (name == assetName)
                    {
                        exe = ExpectedDownload(url, tag, name);
                        size = asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var n) ? n : 0;
                    }
                    else if (name == ChecksumsName)
                    {
                        sums = ExpectedDownload(url, tag, name);
                    }
                }
            }

            if (exe is null || sums is null || size <= 0 || size > MaxDownloadBytes) return null;
            return new UpdateInfo(version, tag, assetName, exe, size, sums, new Uri($"{ProjectInfo.ReleasesUrl}/tag/{tag}"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Finds the SHA-256 for a file in a SHA256SUMS.txt listing.</summary>
    internal static string? ParseChecksum(string listing, string fileName)
    {
        foreach (var raw in listing.Split('\n'))
        {
            var match = ChecksumLine.Match(raw.Trim());
            if (match.Success && match.Groups[2].Value == fileName) return match.Groups[1].Value.ToLowerInvariant();
        }

        return null;
    }

    internal static Version? ParseVersion(string text)
    {
        var tag = text.StartsWith('v') ? text : "v" + text;
        var match = TagPattern.Match(tag);
        return match.Success
            ? new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value))
            : null;
    }

    /// <summary>Downloads and verifies the update. Returns the path of the verified file.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken cancel)
    {
        var listing = Encoding.ASCII.GetString(await GetLimitedAsync(update.ChecksumsUrl, MaxChecksumBytes, cancel));
        var expected = ParseChecksum(listing, update.AssetName)
            ?? throw new InvalidDataException("The release has no checksum for this file.");

        Directory.CreateDirectory(UpdatesFolder);
        CleanFolder();
        var target = Path.Combine(UpdatesFolder, $"Capsule-{update.Version}.exe");
        var partial = target + ".part";

        using (var response = await Http.GetAsync(update.AssetUrl, HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            response.EnsureSuccessStatusCode();
            RequireHttps(response);
            if (response.Content.Headers.ContentLength is long announced && announced != update.AssetSize)
            {
                throw new InvalidDataException("The download size does not match the release.");
            }

            await using var input = await response.Content.ReadAsStreamAsync(cancel);
            await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancel)) > 0)
            {
                total += read;
                if (total > update.AssetSize) throw new InvalidDataException("The download is larger than the release says.");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                progress?.Report((double)total / update.AssetSize);
            }

            if (total != update.AssetSize) throw new InvalidDataException("The download is incomplete.");
            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expected)))
            {
                throw new InvalidDataException("The download failed its checksum check.");
            }
        }

        File.Move(partial, target, overwrite: true);
        Log.Info($"Update {update.Version} downloaded and verified");
        return target;
    }

    /// <summary>
    /// Swaps the running exe for the verified download and starts the new version.
    /// Returns false when that isn't possible (for example the exe is in a protected folder).
    /// </summary>
    public static bool InstallAndRestart(string verifiedExe)
    {
        var current = Environment.ProcessPath;
        if (current is null || !current.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;

        var old = current + ".old";
        try
        {
            if (File.Exists(old)) File.Delete(old);
            File.Move(current, old); // Windows allows renaming a running program
            try
            {
                File.Copy(verifiedExe, current, overwrite: false);
            }
            catch
            {
                File.Move(old, current);
                throw;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Installing the update failed", ex);
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(current) { UseShellExecute = false, ArgumentList = { UpdatedArgument } });
            TryDelete(verifiedExe); // the new copy also cleans the folder when it starts
            Log.Info("Update installed; restarting");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Starting the new version failed", ex);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Cleaned up on the next start.
        }
    }

    /// <summary>Removes the previous exe and leftover downloads after an update.</summary>
    public static void CleanUpAfterUpdate()
    {
        var current = Environment.ProcessPath;
        if (current is not null)
        {
            for (var attempt = 0; attempt < 20 && File.Exists(current + ".old"); attempt++)
            {
                try
                {
                    File.Delete(current + ".old");
                }
                catch
                {
                    Thread.Sleep(250); // the old copy may still be closing
                }
            }
        }

        CleanFolder();
    }

    private static void CleanFolder()
    {
        try
        {
            if (!Directory.Exists(UpdatesFolder)) return;
            foreach (var file in Directory.EnumerateFiles(UpdatesFolder)) File.Delete(file);
        }
        catch
        {
            // Leftovers are harmless.
        }
    }

    private static Uri? ExpectedDownload(string? url, string tag, string? name)
    {
        if (url is null || name is null) return null;
        var expected = ProjectInfo.DownloadPrefix + tag + "/" + name;
        return string.Equals(url, expected, StringComparison.Ordinal) && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri
            : null;
    }

    private static async Task<byte[]> GetLimitedAsync(Uri url, long limit, CancellationToken cancel)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        RequireHttps(response);
        return await ReadLimitedAsync(response, limit, cancel);
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, long limit, CancellationToken cancel)
    {
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Response too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancel);
        using var memory = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancel)) > 0)
        {
            if (memory.Length + read > limit) throw new InvalidDataException("Response too large.");
            memory.Write(buffer, 0, read);
        }

        return memory.ToArray();
    }

    private static void RequireHttps(HttpResponseMessage response)
    {
        if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("Update downloads must use HTTPS.");
        }
    }

    private static bool IsTrue(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true, // GitHub serves release files from its download host
            MaxAutomaticRedirections = 5,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Capsule/{App.Version}");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }
}
