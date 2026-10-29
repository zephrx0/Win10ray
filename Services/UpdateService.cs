using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace XrayMetro.Services;

public sealed record ClientRelease(string Version, string DownloadUrl, string Sha256, long Size, bool IsExperimental = false)
{
    public bool IsInstaller => DownloadUrl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    public string CacheFileName => IsInstaller ? "setup.exe" : "release.zip";
}
public sealed record UpdateHandoff(string TargetDirectory, string ArchivePath, ClientRelease Release, int ParentPid, long ParentStartTicks, bool ExperimentalConfirmed = false);

public sealed class UpdateService : IDisposable
{
    public const string Repository = "zephrx0/Win10ray";
    public const string ReleasesUrl = "https://github.com/" + Repository + "/releases";
    public const long MaximumArchiveBytes = 512L * 1024 * 1024;
    public static string CacheDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win10ray", "Updates");
    private readonly HttpClient _http;

    public UpdateService(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Win10ray-Updater/1.0");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
    }

    public static Version ParseVersion(string text)
    {
        if (!Regex.IsMatch(text, @"^v?\d{1,5}\.\d{1,5}\.\d{1,5}(?:-experimental\.[1-9]\d{0,4})?$") || !Version.TryParse(text.TrimStart('v').Split('-')[0], out Version? version))
            throw new InvalidDataException("Invalid release version.");
        return version;
    }

    public static bool IsExperimentalVersion(string version) { ParseVersion(version); return version.Contains("-experimental.", StringComparison.Ordinal); }
    public static int CompareVersions(string first, string second)
    {
        int result = ParseVersion(first).CompareTo(ParseVersion(second));
        if (result != 0) return result;
        bool a = IsExperimentalVersion(first), b = IsExperimentalVersion(second);
        if (a != b) return a ? -1 : 1;
        return a ? int.Parse(first[(first.LastIndexOf('.') + 1)..], System.Globalization.CultureInfo.InvariantCulture).CompareTo(int.Parse(second[(second.LastIndexOf('.') + 1)..], System.Globalization.CultureInfo.InvariantCulture)) : 0;
    }

    public static ClientRelease? ParseRelease(string json, string currentVersion, bool allowExperimental = false, bool allowStableRollback = false)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean()) return null;
        bool experimental = root.GetProperty("prerelease").GetBoolean();
        if (experimental && !allowExperimental) return null;
        string version = (root.GetProperty("tag_name").GetString() ?? "").TrimStart('v');
        ParseVersion(version);
        if (experimental != IsExperimentalVersion(version)) return null;
        if (CompareVersions(version, currentVersion) <= 0 && !(allowStableRollback && !experimental && IsExperimentalVersion(currentVersion) && ParseVersion(version) >= new Version(1, 4, 0))) return null;
        string[] names = ["Win10ray-Setup-" + version + ".exe", "Win10ray-" + version + "-win-x64.zip"];
        foreach (string name in names)
        foreach (JsonElement asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != name || asset.GetProperty("state").GetString() != "uploaded") continue;
            string digest = asset.GetProperty("digest").GetString() ?? "";
            var release = new ClientRelease(version, asset.GetProperty("browser_download_url").GetString() ?? "", digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest[7..] : "", asset.GetProperty("size").GetInt64(), experimental);
            Validate(release);
            return release;
        }
        throw new InvalidDataException("The release has no compatible Windows installer.");
    }

    public static void Validate(ClientRelease release)
    {
        ParseVersion(release.Version);
        if (release.IsExperimental != IsExperimentalVersion(release.Version)) throw new InvalidDataException("Release channel mismatch.");
        if (!Regex.IsMatch(release.Sha256, "^[a-fA-F0-9]{64}$") || release.Size is < 1 or > MaximumArchiveBytes)
            throw new InvalidDataException("Invalid release checksum or size.");
        string name = release.IsInstaller ? "Win10ray-Setup-" + release.Version + ".exe" : "Win10ray-" + release.Version + "-win-x64.zip";
        string expected = ReleasesUrl + "/download/v" + release.Version + "/" + name;
        if (!string.Equals(release.DownloadUrl, expected, StringComparison.Ordinal)) throw new InvalidDataException("Unexpected release source.");
    }

    public async Task<ClientRelease?> CheckAsync(string currentVersion, CancellationToken cancellationToken = default, bool experimental = false)
    {
        ParseVersion(currentVersion);
        if (!experimental) return ParseRelease(await MetadataAsync("/releases/latest", cancellationToken).ConfigureAwait(false) ?? "{\"draft\":true}", currentVersion, allowStableRollback: IsExperimentalVersion(currentVersion));
        ClientRelease? best = null;
        for (int page = 1; page <= 5; page++)
        {
            string? json = await MetadataAsync("/releases?per_page=100&page=" + page, cancellationToken).ConfigureAwait(false);
            if (json is null) break;
            using JsonDocument document = JsonDocument.Parse(json);
            foreach (JsonElement item in document.RootElement.EnumerateArray())
            {
                ClientRelease? release;
                try { release = ParseRelease(item.GetRawText(), "0.0.0", allowExperimental: true); }
                catch (Exception exception) when (exception is InvalidDataException or KeyNotFoundException or InvalidOperationException) { continue; }
                if (release is not { IsExperimental: true }) continue;
                bool eligible = CompareVersions(release.Version, currentVersion) > 0 ||
                    !IsExperimentalVersion(currentVersion) && ParseVersion(release.Version) >= ParseVersion(currentVersion);
                if (eligible && (best is null || CompareVersions(release.Version, best.Version) > 0)) best = release;
            }
            if (document.RootElement.GetArrayLength() < 100) break;
        }
        return best;
    }

    private async Task<string?> MetadataAsync(string path, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/" + Repository + path);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 1024 * 1024) throw new InvalidDataException("Release metadata is too large.");
        await using Stream input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192]; int count;
        while ((count = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > 1024 * 1024) throw new InvalidDataException("Release metadata is too large.");
            output.Write(buffer, 0, count);
        }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }

    public async Task<string> DownloadAsync(ClientRelease release, string directory, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        Validate(release);
        Directory.CreateDirectory(directory);
        string archive = Path.Combine(directory, release.CacheFileName);
        bool created = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            Uri uri = new(release.DownloadUrl);
            for (int redirects = 0; redirects <= 5; redirects++)
            {
                if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
                    uri.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                    throw new InvalidDataException("Unexpected download host.");
                using HttpResponseMessage response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    uri = new Uri(uri, response.Headers.Location ?? throw new InvalidDataException("Missing redirect location."));
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long length && length != release.Size) throw new InvalidDataException("Download size mismatch.");
                await using Stream source = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                await using (var target = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    created = true;
                    byte[] buffer = new byte[65536]; long total = 0; int count;
                    while ((count = await source.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
                    {
                        total += count;
                        if (total > release.Size) throw new InvalidDataException("Download exceeds declared size.");
                        await target.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
                        progress?.Report(100.0 * total / release.Size);
                    }
                    if (total != release.Size) throw new InvalidDataException("Incomplete download.");
                }
                await VerifyAsync(archive, release, deadline.Token).ConfigureAwait(false);
                return archive;
            }
            throw new InvalidDataException("Too many download redirects.");
        }
        catch { if (created && File.Exists(archive)) File.Delete(archive); throw; }
    }

    public static async Task VerifyAsync(string archive, ClientRelease release, CancellationToken token = default)
    {
        Validate(release);
        await using var stream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (stream.Length != release.Size || !Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Release checksum mismatch.");
    }

    public void Dispose() => _http.Dispose();

    public static async Task CleanupCompletedAsync()
    {
        await Task.Delay(5000).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(CacheDirectory)) return;
            foreach (string directory in Directory.GetDirectories(CacheDirectory))
            {
                if (!Regex.IsMatch(Path.GetFileName(directory), "^[a-f0-9]{32}$")) continue;
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                    if (File.Exists(Path.Combine(directory, "complete")) || Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-7))
                        Directory.Delete(directory, true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
