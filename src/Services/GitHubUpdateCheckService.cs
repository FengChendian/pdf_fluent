using System.Net.Http;
using System.Xml.Linq;

namespace pdf_fluent.Services;

public interface IUpdateCheckService
{
    Task<UpdateCheckResult> CheckForUpdateAsync(Version currentVersion, CancellationToken cancellationToken = default);
}

public sealed record UpdateCheckResult(string LatestVersion, string ReleaseUrl, bool IsUpdateAvailable);

public sealed class GitHubUpdateCheckService : IUpdateCheckService, IDisposable
{
    internal const string ReleasesFeedUrl = "https://github.com/FengChendian/pdf_fluent/releases.atom";
    internal const string ReleasesPageUrl = "https://github.com/FengChendian/pdf_fluent/releases";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _timeout;

    public GitHubUpdateCheckService() : this(new HttpClient(), DefaultTimeout)
    {
    }

    internal GitHubUpdateCheckService(HttpClient httpClient, TimeSpan timeout)
    {
        _httpClient = httpClient;
        _timeout = timeout;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Fluent-PDF-Update-Checker");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/atom+xml");
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);
        using HttpResponseMessage response = await _httpClient.GetAsync(
            ReleasesFeedUrl, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);
        response.EnsureSuccessStatusCode();

        string xml = await response.Content.ReadAsStringAsync(timeoutSource.Token);
        XDocument feed = XDocument.Parse(xml);
        XNamespace atom = "http://www.w3.org/2005/Atom";
        XElement? entry = feed.Root?.Element(atom + "entry");
        string? entryId = entry?.Element(atom + "id")?.Value;
        string? tagName = entryId?.Split('/').LastOrDefault();
        string? releaseUrl = entry?.Elements(atom + "link")
            .FirstOrDefault(link => link.Attribute("rel")?.Value == "alternate")
            ?.Attribute("href")?.Value;
        if (string.IsNullOrWhiteSpace(tagName))
            throw new InvalidDataException("GitHub 未返回有效的版本标签。");

        releaseUrl = Uri.TryCreate(releaseUrl, UriKind.Absolute, out Uri? uri) &&
                     (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? releaseUrl
            : ReleasesPageUrl;
        return new UpdateCheckResult(
            tagName,
            releaseUrl,
            ReleaseVersionComparer.Compare(tagName, currentVersion) > 0);
    }

    public void Dispose() => _httpClient.Dispose();
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

        int coreComparison = CompareCore(releaseVersion, currentVersion);
        if (coreComparison != 0 || versionParts.Length == 1) return coreComparison;
        return -1;
    }

    private static int CompareCore(Version left, Version right)
    {
        int[] leftParts = [left.Major, left.Minor, Math.Max(left.Build, 0), Math.Max(left.Revision, 0)];
        int[] rightParts = [right.Major, right.Minor, Math.Max(right.Build, 0), Math.Max(right.Revision, 0)];
        for (int index = 0; index < leftParts.Length; index++)
        {
            int comparison = leftParts[index].CompareTo(rightParts[index]);
            if (comparison != 0) return comparison;
        }
        return 0;
    }
}
