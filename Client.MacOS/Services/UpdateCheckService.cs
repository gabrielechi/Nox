using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Client.MacOS.Services;

/// <summary>A file attached to a GitHub release, with the SHA-256 digest GitHub published for it.</summary>
internal sealed record ReleaseAsset(string Name, Uri Url, long Size, string Sha256);

/// <summary>A release on GitHub that is newer than the running client and carries the macOS files.</summary>
internal sealed record AvailableUpdate(
    Version Version,
    string Tag,
    string ReleasePageUrl,
    ReleaseAsset Package,
    ReleaseAsset Installer);

/// <summary>
/// macOS counterpart of Client.Windows/Services/UpdateCheckService.cs: same repository, same tag rules,
/// same SHA-256 check against the digest published by GitHub. Instead of NOX-Setup-*.exe a release must
/// carry NOX-macOS-arm64.zip (the app) and install-nox.sh (the installer that swaps the app bundle).
/// A release without both files is not offered on macOS. The check is best effort: any network or
/// format problem simply means "no update".
/// </summary>
internal sealed class UpdateCheckService : IDisposable
{
    public const string PackageName = "NOX-macOS-arm64.zip";
    public const string InstallerName = "install-nox.sh";

    private const string DefaultLatestReleaseApi = "https://api.github.com/repos/gabrielechi/Nox/releases/latest";
    // Files are only accepted from this repository's release downloads.
    private const string DefaultDownloadPrefix = "https://github.com/gabrielechi/Nox/releases/download/";
    private const string ReleasePagePrefix = "https://github.com/gabrielechi/Nox/releases/";
    private const long MaximumPackageSize = 512L * 1024 * 1024;
    private const long MaximumInstallerSize = 1024 * 1024;
    private static readonly Regex Sha256Digest = new(@"^sha256:([0-9a-fA-F]{64})$");

    private readonly HttpClient _http;
    private readonly string _latestReleaseApi;
    private readonly string _downloadPrefix;

    public UpdateCheckService(Version currentVersion)
    {
        CurrentVersion = Normalize(currentVersion);
        _latestReleaseApi = DefaultLatestReleaseApi;
        _downloadPrefix = DefaultDownloadPrefix;
#if DEBUG
        // Debug builds only: lets a local test server stand in for GitHub.
        _latestReleaseApi = Environment.GetEnvironmentVariable("NOX_UPDATE_API") is { Length: > 0 } api ? api : _latestReleaseApi;
        _downloadPrefix = Environment.GetEnvironmentVariable("NOX_UPDATE_DOWNLOAD_PREFIX") is { Length: > 0 } prefix ? prefix : _downloadPrefix;
#endif
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NOX-Client-macOS", CurrentVersion.ToString()));
    }

    public Version CurrentVersion { get; }

    /// <summary>Returns the latest release when it is newer than the running client, otherwise null.</summary>
    public async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var request = new HttpRequestMessage(HttpMethod.Get, _latestReleaseApi);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using HttpResponseMessage response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using JsonDocument json = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token).ConfigureAwait(false);
            return ParseRelease(json.RootElement, CurrentVersion, _downloadPrefix);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException
                                       or JsonException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    internal static AvailableUpdate? ParseRelease(JsonElement release, Version currentVersion,
        string downloadPrefix = DefaultDownloadPrefix)
    {
        if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
        if (release.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;
        if (!TryGetString(release, "tag_name", out string tag) || !TryParseTag(tag, out Version latest)) return null;
        if (latest <= Normalize(currentVersion)) return null;
        if (!TryGetString(release, "html_url", out string page) ||
            !page.StartsWith(ReleasePagePrefix, StringComparison.OrdinalIgnoreCase)) return null;
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

        ReleaseAsset? package = null, installer = null;
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            if (!TryGetString(asset, "name", out string name)) continue;
            long limit = name == PackageName ? MaximumPackageSize : name == InstallerName ? MaximumInstallerSize : 0;
            if (limit == 0) continue;
            if (!TryGetString(asset, "browser_download_url", out string url) ||
                !url.StartsWith(downloadPrefix, StringComparison.Ordinal) ||
                !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) continue;
            if (!TryGetString(asset, "digest", out string digest)) continue;
            Match match = Sha256Digest.Match(digest);
            if (!match.Success) continue;
            long size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out long v) ? v : 0;
            if (size <= 0 || size > limit) continue;

            var parsed = new ReleaseAsset(name, uri, size, match.Groups[1].Value.ToLowerInvariant());
            if (name == PackageName) package = parsed;
            else installer = parsed;
        }
        return package is not null && installer is not null
            ? new AvailableUpdate(latest, tag, page, package, installer)
            : null;
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
    /// Downloads the installer script and the app package into a private temporary folder and verifies
    /// both against the SHA-256 digests published by GitHub. Progress covers the whole download.
    /// </summary>
    public async Task<(string Installer, string Package)> DownloadAsync(AvailableUpdate update,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        string folder = Path.Combine(Path.GetTempPath(), "NOX", "Updates", update.Version.ToString());
        Directory.CreateDirectory(folder);
        long total = update.Installer.Size + update.Package.Size;
        long done = 0;
        var overall = new Progress<long>(bytes => progress?.Report(Math.Min(1.0, (double)(done + bytes) / total)));

        string installer = await DownloadAssetAsync(update.Installer, folder, overall, cancellationToken).ConfigureAwait(false);
        done = update.Installer.Size;
        string package = await DownloadAssetAsync(update.Package, folder, overall, cancellationToken).ConfigureAwait(false);
        progress?.Report(1.0);
        return (installer, package);
    }

    private async Task<string> DownloadAssetAsync(ReleaseAsset asset, string folder, IProgress<long> progress,
        CancellationToken cancellationToken)
    {
        long limit = asset.Name == PackageName ? MaximumPackageSize : MaximumInstallerSize;
        string target = Path.Combine(folder, asset.Name);
        string partial = target + ".part";

        try
        {
            using HttpResponseMessage response = await _http.GetAsync(asset.Url,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if ((response.Content.Headers.ContentLength ?? asset.Size) > limit)
                throw new InvalidDataException("Download too large.");

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                byte[] buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    received += read;
                    if (received > limit) throw new InvalidDataException("Download too large.");
                    sha.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress.Report(received);
                }
            }

            string actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(actual), System.Text.Encoding.ASCII.GetBytes(asset.Sha256)))
                throw new UpdateIntegrityException();

            File.Move(partial, target, overwrite: true);
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

/// <summary>A downloaded file does not match the SHA-256 digest published with the release.</summary>
internal sealed class UpdateIntegrityException : Exception
{
}
