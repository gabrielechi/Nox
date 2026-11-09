using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Client.Windows.Services;

/// <summary>A release on GitHub that is newer than the running client.</summary>
internal sealed record AvailableUpdate(
    Version Version,
    string Tag,
    string ReleasePageUrl,
    string InstallerName,
    Uri InstallerUrl,
    long InstallerSize,
    string InstallerSha256);

/// <summary>
/// Checks the public NOX release repository for a newer client and downloads its installer.
/// The check is best effort: any network or format problem simply means "no update".
/// </summary>
internal sealed class UpdateCheckService : IDisposable
{
    private const string LatestReleaseApi = "https://api.github.com/repos/gabrielechi/Nox/releases/latest";
    // Installers are only accepted from this repository's release downloads.
    private const string AllowedDownloadPrefix = "https://github.com/gabrielechi/Nox/releases/download/";
    private const long MaximumInstallerSize = 512L * 1024 * 1024;
    private static readonly Regex InstallerName = new(@"^NOX-Setup-[0-9A-Za-z.\-]+\.exe$", RegexOptions.IgnoreCase);
    private static readonly Regex Sha256Digest = new(@"^sha256:([0-9a-fA-F]{64})$");

    private readonly HttpClient _http;

    public UpdateCheckService(Version currentVersion)
    {
        CurrentVersion = Normalize(currentVersion);
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NOX-Client", CurrentVersion.ToString()));
    }

    public Version CurrentVersion { get; }

    /// <summary>Returns the latest release when it is newer than the running client, otherwise null.</summary>
    public async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using HttpResponseMessage response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using JsonDocument json = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token).ConfigureAwait(false);
            return ParseRelease(json.RootElement, CurrentVersion);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException
                                       or JsonException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    internal static AvailableUpdate? ParseRelease(JsonElement release, Version currentVersion)
    {
        if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
        if (release.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;
        if (!TryGetString(release, "tag_name", out string tag) || !TryParseTag(tag, out Version latest)) return null;
        if (latest <= Normalize(currentVersion)) return null;
        if (!TryGetString(release, "html_url", out string page) ||
            !page.StartsWith("https://github.com/gabrielechi/Nox/releases/", StringComparison.OrdinalIgnoreCase)) return null;
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

        foreach (JsonElement asset in assets.EnumerateArray())
        {
            if (!TryGetString(asset, "name", out string name) || !InstallerName.IsMatch(name)) continue;
            if (!TryGetString(asset, "browser_download_url", out string url) ||
                !url.StartsWith(AllowedDownloadPrefix, StringComparison.Ordinal) ||
                !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) continue;
            if (!TryGetString(asset, "digest", out string digest)) continue;
            Match match = Sha256Digest.Match(digest);
            if (!match.Success) continue;
            long size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out long v) ? v : 0;
            if (size <= 0 || size > MaximumInstallerSize) continue;
            return new AvailableUpdate(latest, tag, page, name, uri, size, match.Groups[1].Value.ToLowerInvariant());
        }
        return null;
    }

    /// <summary>"v.3.1", "v3.1", "3.1.0" → 3.1.0.</summary>
    internal static bool TryParseTag(string tag, out Version version)
    {
        string text = tag.Trim().TrimStart('v', 'V').TrimStart('.');
        int end = 0;
        while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '.')) end++;
        if (Version.TryParse(text[..end].TrimEnd('.'), out Version? parsed))
        {
            version = Normalize(parsed);
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }

    internal static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    /// <summary>3.1.0 → "v3.1", 3.1.2 → "v3.1.2".</summary>
    internal static string Display(Version v) => v.Build > 0 ? $"v{v.Major}.{v.Minor}.{v.Build}" : $"v{v.Major}.{v.Minor}";

    /// <summary>
    /// Downloads the installer into a private temporary folder and verifies its SHA-256 against the digest
    /// published by GitHub. Returns the path of the verified file.
    /// </summary>
    public async Task<string> DownloadInstallerAsync(AvailableUpdate update, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        string folder = Path.Combine(Path.GetTempPath(), "NOX", "Updates");
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, update.InstallerName);
        string partial = target + ".part";

        try
        {
            using HttpResponseMessage response = await _http.GetAsync(update.InstallerUrl,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? update.InstallerSize;
            if (total > MaximumInstallerSize) throw new InvalidDataException("Installer too large.");

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                byte[] buffer = new byte[81920];
                long received = 0;
                int read, lastPercent = -1;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    received += read;
                    if (received > MaximumInstallerSize) throw new InvalidDataException("Installer too large.");
                    sha.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    int percent = total > 0 ? (int)Math.Min(100, received * 100 / total) : 0;
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress?.Report(percent / 100.0);
                    }
                }
            }

            string actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(actual), System.Text.Encoding.ASCII.GetBytes(update.InstallerSha256)))
                throw new UpdateIntegrityException();

            File.Move(partial, target, overwrite: true);
            progress?.Report(1.0);
            return target;
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        if (element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } s)
        {
            value = s;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>The downloaded installer does not match the SHA-256 digest published with the release.</summary>
internal sealed class UpdateIntegrityException : Exception
{
}
