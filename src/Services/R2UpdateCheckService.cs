using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace pdf_fluent.Services;

public interface IUpdateCheckService
{
    Task<UpdateCheckResult> CheckForUpdateAsync(Version currentVersion, CancellationToken cancellationToken = default);
}

public sealed record UpdateCheckResult(string LatestVersion, bool IsUpdateAvailable, UpdatePackage Package);

public sealed record UpdatePackage(string Version, Uri DownloadUri, long Size, string Sha256)
{
    internal void Validate()
    {
        if (!Regex.IsMatch(Version, @"^\d+\.\d+\.\d+$") || !System.Version.TryParse(Version, out _) ||
            Size <= 0 || Size > 2L * 1024 * 1024 * 1024 || !Regex.IsMatch(Sha256, "^[a-fA-F0-9]{64}$"))
            throw new InvalidDataException("更新清单中的版本、大小或校验值无效。");

        if (!DownloadUri.IsAbsoluteUri || DownloadUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(DownloadUri.Host, R2UpdateCheckService.DownloadHost, StringComparison.OrdinalIgnoreCase) ||
            !DownloadUri.IsDefaultPort || DownloadUri.UserInfo.Length != 0 ||
            DownloadUri.Query.Length != 0 || DownloadUri.Fragment.Length != 0 ||
            Uri.UnescapeDataString(DownloadUri.AbsolutePath) != $"/Fluent PDF Setup {Version} win-x64.exe")
            throw new InvalidDataException("更新清单中的安装包地址无效。");
    }
}

public sealed class R2UpdateCheckService : IUpdateCheckService, IDisposable
{
    internal const string DownloadHost = "pdf-fluent.yichen.xyz";
    internal const string ManifestUrl = "https://pdf-fluent.yichen.xyz/config.json";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _timeout;

    public R2UpdateCheckService() : this(
        new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }), DefaultTimeout)
    {
    }

    internal R2UpdateCheckService(HttpClient httpClient, TimeSpan timeout)
    {
        _httpClient = httpClient;
        _timeout = timeout;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Fluent-PDF-Update-Checker");
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(
        Version currentVersion, CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUrl);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);
        response.EnsureSuccessStatusCode();
        await using Stream content = await response.Content.ReadAsStreamAsync(timeoutSource.Token);
        UpdateManifest? manifest = await JsonSerializer.DeserializeAsync<UpdateManifest>(
            content, JsonOptions, timeoutSource.Token);
        if (manifest is null || manifest.SchemaVersion != 1 ||
            string.IsNullOrWhiteSpace(manifest.Version) || string.IsNullOrWhiteSpace(manifest.Sha256) ||
            !Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out Uri? downloadUri))
            throw new InvalidDataException("未获取到有效的更新清单。");

        var package = new UpdatePackage(manifest.Version, downloadUri, manifest.Size, manifest.Sha256);
        package.Validate();
        return new UpdateCheckResult($"v{package.Version}",
            ReleaseVersionComparer.Compare(package.Version, currentVersion) > 0, package);
    }

    public void Dispose() => _httpClient.Dispose();

    private sealed record UpdateManifest(int SchemaVersion, string? Version, string? DownloadUrl, long Size, string? Sha256);
}

internal static class ReleaseVersionComparer
{
    public static int Compare(string releaseTag, Version currentVersion)
    {
        string tag = releaseTag.Trim().TrimStart('v', 'V');
        int buildSeparator = tag.IndexOf('+');
        if (buildSeparator >= 0) tag = tag[..buildSeparator];
        string[] versionParts = tag.Split('-', 2);
        if (!Version.TryParse(versionParts[0], out Version? releaseVersion))
            throw new InvalidDataException($"无法识别版本标签：{releaseTag}");

        int[] left = [releaseVersion.Major, releaseVersion.Minor, Math.Max(releaseVersion.Build, 0), Math.Max(releaseVersion.Revision, 0)];
        int[] right = [currentVersion.Major, currentVersion.Minor, Math.Max(currentVersion.Build, 0), Math.Max(currentVersion.Revision, 0)];
        for (int index = 0; index < left.Length; index++)
        {
            int comparison = left[index].CompareTo(right[index]);
            if (comparison != 0) return comparison;
        }
        return versionParts.Length == 1 ? 0 : -1;
    }
}
