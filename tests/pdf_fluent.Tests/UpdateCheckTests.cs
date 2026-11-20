using System.Net;
using System.Net.Http;
using pdf_fluent.Services;
using pdf_fluent.ViewModels;
using Xunit;

namespace pdf_fluent.Tests;

public sealed class UpdateCheckTests
{
    [Theory]
    [InlineData("v0.5.0", true)]
    [InlineData("0.4.3", true)]
    [InlineData("v0.4.2", false)]
    [InlineData("v0.4.1", false)]
    [InlineData("v0.4.2-beta.1", false)]
    [InlineData("v0.4.2+build.7", false)]
    public async Task LatestReleaseTagIsComparedWithTheCurrentVersion(string tag, bool expectedUpdate)
    {
        using var httpClient = new HttpClient(new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"<feed xmlns=\"http://www.w3.org/2005/Atom\"><entry>" +
                $"<id>tag:github.com,2008:Repository/123456/{tag}</id>" +
                $"<link rel=\"alternate\" type=\"text/html\" href=\"https://github.com/FengChendian/pdf_fluent/releases/tag/{tag}\" />" +
                $"<title>Fluent PDF {tag}</title></entry></feed>"),
        })));
        using var service = new GitHubUpdateCheckService(httpClient, TimeSpan.FromSeconds(1));

        UpdateCheckResult result = await service.CheckForUpdateAsync(
            new Version(0, 4, 2), TestContext.Current.CancellationToken);

        Assert.Equal(expectedUpdate, result.IsUpdateAvailable);
        Assert.Equal(tag, result.LatestVersion);
        Assert.EndsWith($"/tag/{tag}", result.ReleaseUrl);
    }

    [Fact]
    public async Task UpdateServiceUsesTheRepositoryReleasesFeedAndTimesOut()
    {
        HttpRequestMessage? request = null;
        using var httpClient = new HttpClient(new StubHttpHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        })
        {
            OnSend = message => request = message,
        });
        using var service = new GitHubUpdateCheckService(httpClient, TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.CheckForUpdateAsync(new Version(0, 4, 2), TestContext.Current.CancellationToken));

        Assert.NotNull(request);
        Assert.Equal(GitHubUpdateCheckService.ReleasesFeedUrl, request.RequestUri?.ToString());
        Assert.Contains(request.Headers.UserAgent, value => value.Product?.Name == "Fluent-PDF-Update-Checker");
    }

    [Fact]
    public async Task UpdateViewModelShowsProgressThenExposesTheDownloadAction()
    {
        var completion = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new StubUpdateService((_, _) => completion.Task);
        string? openedUrl = null;
        using var viewModel = new SettingsViewModel(service, new Version(0, 4, 2), url => openedUrl = url);

        Task check = viewModel.CheckForUpdatesOnStartupAsync();
        Assert.True(viewModel.IsCheckingUpdate);
        Assert.Equal("检查中", viewModel.UpdateButtonText);
        await viewModel.CheckForUpdatesOnStartupAsync();
        Assert.Equal(1, service.CallCount);

        completion.SetResult(new UpdateCheckResult(
            "v0.5.0", "https://github.com/FengChendian/pdf_fluent/releases/tag/v0.5.0", true));
        await check;

        Assert.False(viewModel.IsCheckingUpdate);
        Assert.True(viewModel.HasAvailableUpdate);
        Assert.Equal("前往下载", viewModel.UpdateButtonText);
        Assert.Equal("发现新版本 v0.5.0", viewModel.UpdateStatusMessage);
        await viewModel.UpdateActionCommand.ExecuteAsync(null);
        Assert.Equal("https://github.com/FengChendian/pdf_fluent/releases/tag/v0.5.0", openedUrl);
    }

    [Fact]
    public async Task UpToDateAndFailedChecksKeepTheCheckUpdateAction()
    {
        using var upToDate = new SettingsViewModel(
            new StubUpdateService((_, _) => Task.FromResult(new UpdateCheckResult(
                "v0.4.2", GitHubUpdateCheckService.ReleasesPageUrl, false))),
            new Version(0, 4, 2));
        await upToDate.CheckForUpdatesOnStartupAsync();
        Assert.Equal("检查更新", upToDate.UpdateButtonText);
        Assert.False(upToDate.HasAvailableUpdate);
        Assert.Equal("当前已是最新版本", upToDate.UpdateStatusMessage);

        using var failed = new SettingsViewModel(
            new StubUpdateService((_, _) => Task.FromException<UpdateCheckResult>(new HttpRequestException())),
            new Version(0, 4, 2));
        await failed.CheckForUpdatesOnStartupAsync();
        Assert.Equal("检查更新", failed.UpdateButtonText);
        Assert.False(failed.HasAvailableUpdate);
        Assert.False(failed.IsCheckingUpdate);
        Assert.Equal("检查失败，请检查网络后重试", failed.UpdateStatusMessage);

        using var timedOut = new SettingsViewModel(
            new StubUpdateService((_, _) => Task.FromCanceled<UpdateCheckResult>(new CancellationToken(true))),
            new Version(0, 4, 2));
        await timedOut.CheckForUpdatesOnStartupAsync();
        Assert.Equal("检查更新", timedOut.UpdateButtonText);
        Assert.Equal("检查超时，请稍后重试", timedOut.UpdateStatusMessage);
    }

    private sealed class StubUpdateService(
        Func<Version, CancellationToken, Task<UpdateCheckResult>> check) : IUpdateCheckService
    {
        public int CallCount { get; private set; }

        public Task<UpdateCheckResult> CheckForUpdateAsync(
            Version currentVersion,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return check(currentVersion, cancellationToken);
        }
    }

    private sealed class StubHttpHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public Action<HttpRequestMessage>? OnSend { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            OnSend?.Invoke(request);
            return send(request, cancellationToken);
        }
    }
}
