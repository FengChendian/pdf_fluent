using System.Net;
using System.Net.Http;
using System.Text.Json;
using pdf_fluent.Services;
using pdf_fluent.ViewModels;
using Xunit;

namespace pdf_fluent.Tests;

public sealed class UpdateCheckTests
{
    [Theory]
    [InlineData("0.5.0", true)]
    [InlineData("0.4.3", true)]
    [InlineData("0.4.2", false)]
    [InlineData("0.4.1", false)]
    [InlineData("0.10.0", true)]
    public async Task ManifestVersionIsComparedWithTheCurrentVersion(string version, bool expectedUpdate)
    {
        using var client = UpdateTestData.Client(UpdateTestData.Manifest(version));
        using var service = new R2UpdateCheckService(client, TimeSpan.FromSeconds(1));
        UpdateCheckResult result = await service.CheckForUpdateAsync(
            new Version(0, 4, 2), TestContext.Current.CancellationToken);
        Assert.Equal(expectedUpdate, result.IsUpdateAvailable);
        Assert.Equal($"v{version}", result.LatestVersion);
        Assert.Equal(UpdateTestData.Package(version), result.Package);
    }

    [Theory]
    [InlineData("v0.4.2-beta.1", false)]
    [InlineData("v0.4.2+build.7", false)]
    [InlineData("v0.10.0", true)]
    public void VersionComparisonSupportsExistingTags(string tag, bool expectedUpdate) =>
        Assert.Equal(expectedUpdate, ReleaseVersionComparer.Compare(tag, new Version(0, 4, 2)) > 0);

    [Theory]
    [InlineData(2, "https://pdf-fluent.yichen.xyz/Fluent%20PDF%20Setup%200.5.0%20win-x64.exe", 1, "valid")]
    [InlineData(1, "https://other.example/Fluent%20PDF%20Setup%200.5.0%20win-x64.exe", 1, "valid")]
    [InlineData(1, "http://pdf-fluent.yichen.xyz/Fluent%20PDF%20Setup%200.5.0%20win-x64.exe", 1, "valid")]
    [InlineData(1, "https://pdf-fluent.yichen.xyz/Fluent%20PDF%20Setup%200.4.0%20win-x64.exe", 1, "valid")]
    [InlineData(1, "https://pdf-fluent.yichen.xyz/Fluent%20PDF%20Setup%200.5.0%20win-x64.exe", 0, "valid")]
    [InlineData(1, "https://pdf-fluent.yichen.xyz/Fluent%20PDF%20Setup%200.5.0%20win-x64.exe", 1, "invalid")]
    public async Task InvalidOrUntrustedManifestsAreRejected(int schemaVersion, string url, long size, string hash)
    {
        string json = JsonSerializer.Serialize(new
        {
            schemaVersion, version = "0.5.0", downloadUrl = url, size,
            sha256 = hash == "valid" ? UpdateTestData.Hash : hash,
        });
        using var client = UpdateTestData.Client(json);
        using var service = new R2UpdateCheckService(client, TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckForUpdateAsync(
            new Version(0, 4, 2), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ManifestRequestBypassesCacheAndTimesOut()
    {
        Uri? requestUri = null;
        bool noCache = false;
        using var client = new HttpClient(new UpdateTestData.HttpHandler(async (request, token) =>
        {
            requestUri = request.RequestUri;
            noCache = request.Headers.CacheControl is { NoCache: true, NoStore: true };
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable");
        }));
        using var service = new R2UpdateCheckService(client, TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CheckForUpdateAsync(
            new Version(0, 4, 2), TestContext.Current.CancellationToken));
        Assert.Equal(R2UpdateCheckService.ManifestUrl, requestUri?.AbsoluteUri);
        Assert.True(noCache);
    }

    [Fact]
    public async Task UpdateActionDownloadsBeforeRequestingShutdownAndBlocksDuplicateActions()
    {
        var check = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var download = new TaskCompletionSource<IPreparedUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var installer = new UpdateTestData.Installer((_, _, _) => download.Task);
        using var viewModel = new SettingsViewModel(new UpdateTestData.Checker(check.Task), new Version(0, 4, 2), installer);
        int shutdownRequests = 0;
        viewModel.CloseForUpdateAsync = commit =>
        {
            shutdownRequests++;
            commit();
            return Task.FromResult(true);
        };
        Task checking = viewModel.CheckForUpdatesOnStartupAsync();
        Assert.True(viewModel.IsBusy);
        Assert.Equal("检查中", viewModel.UpdateButtonText);
        check.SetResult(UpdateTestData.Result());
        await checking;
        Assert.Equal("下载并更新", viewModel.UpdateButtonText);

        Task updating = viewModel.UpdateActionCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsInstallingUpdate);
        Assert.False(viewModel.UpdateActionCommand.CanExecute(null));
        Assert.False(viewModel.DownloadUpdateCommand.CanExecute(null));
        Assert.Equal(0, shutdownRequests);
        var prepared = new UpdateTestData.PreparedUpdate();
        download.SetResult(prepared);
        await updating;
        Assert.Equal(1, shutdownRequests);
        Assert.True(prepared.Committed);
        Assert.True(prepared.Disposed);
    }

    [Fact]
    public async Task CancellingUnsavedDocumentPromptDisposesTheUncommittedUpdate()
    {
        var prepared = new UpdateTestData.PreparedUpdate();
        var installer = new UpdateTestData.Installer((_, _, _) => Task.FromResult<IPreparedUpdate>(prepared));
        using var viewModel = new SettingsViewModel(new UpdateTestData.Checker(Task.FromResult(UpdateTestData.Result())),
            new Version(0, 4, 2), installer) { CloseForUpdateAsync = _ => Task.FromResult(false) };
        await viewModel.CheckForUpdatesOnStartupAsync();
        await viewModel.UpdateActionCommand.ExecuteAsync(null);
        Assert.False(prepared.Committed);
        Assert.True(prepared.Disposed);
        Assert.False(viewModel.IsBusy);
        Assert.Equal("已取消更新，应用将保持打开", viewModel.UpdateStatusMessage);
    }

    [Fact]
    public async Task FailedVerificationNeverRequestsShutdownAndAllowsRetry()
    {
        var installer = new UpdateTestData.Installer((_, _, _) =>
            Task.FromException<IPreparedUpdate>(new InvalidDataException("checksum mismatch")));
        bool closed = false;
        using var viewModel = new SettingsViewModel(new UpdateTestData.Checker(Task.FromResult(UpdateTestData.Result())),
            new Version(0, 4, 2), installer)
        {
            CloseForUpdateAsync = _ => { closed = true; return Task.FromResult(true); },
        };
        await viewModel.CheckForUpdatesOnStartupAsync();
        await viewModel.UpdateActionCommand.ExecuteAsync(null);
        Assert.False(closed);
        Assert.True(viewModel.HasAvailableUpdate);
        Assert.True(viewModel.UpdateActionCommand.CanExecute(null));
        Assert.Equal("安装包校验失败，请重新下载", viewModel.UpdateStatusMessage);
    }

    [Fact]
    public async Task UpToDateAndFailedChecksKeepTheCheckUpdateAction()
    {
        using var upToDate = new SettingsViewModel(new UpdateTestData.Checker(
            Task.FromResult(UpdateTestData.Result("0.4.2", false))), new Version(0, 4, 2));
        await upToDate.CheckForUpdatesOnStartupAsync();
        Assert.Equal("检查更新", upToDate.UpdateButtonText);
        Assert.False(upToDate.HasAvailableUpdate);
        Assert.Equal("当前已是最新版本", upToDate.UpdateStatusMessage);
        using var failed = new SettingsViewModel(new UpdateTestData.Checker(
            Task.FromException<UpdateCheckResult>(new HttpRequestException())), new Version(0, 4, 2));
        await failed.CheckForUpdatesOnStartupAsync();
        Assert.Equal("检查更新", failed.UpdateButtonText);
        Assert.False(failed.IsBusy);
        Assert.Equal("检查失败，请检查网络后重试", failed.UpdateStatusMessage);
        using var timedOut = new SettingsViewModel(new UpdateTestData.Checker(
            Task.FromCanceled<UpdateCheckResult>(new CancellationToken(true))), new Version(0, 4, 2));
        await timedOut.CheckForUpdatesOnStartupAsync();
        Assert.Equal("检查超时，请稍后重试", timedOut.UpdateStatusMessage);
    }
}

internal static class UpdateTestData
{
    public static string Hash => new('a', 64);
    public static UpdatePackage Package(string version = "0.5.0") => new(version,
        new Uri($"https://pdf-fluent.yichen.xyz/{Uri.EscapeDataString($"Fluent PDF Setup {version} win-x64.exe")}"), 1, Hash);
    public static UpdateCheckResult Result(string version = "0.5.0", bool available = true) => new($"v{version}", available, Package(version));
    public static string Manifest(string version) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1, version, downloadUrl = Package(version).DownloadUri.AbsoluteUri, size = 1, sha256 = Hash,
    });
    public static HttpClient Client(string content) => new(new HttpHandler((_, _) => Task.FromResult(
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) })));

    internal sealed class HttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
    internal sealed class Checker(Task<UpdateCheckResult> result) : IUpdateCheckService
    {
        public Task<UpdateCheckResult> CheckForUpdateAsync(Version currentVersion, CancellationToken cancellationToken = default) => result;
    }
    internal sealed class Installer(Func<UpdatePackage, IProgress<int>?, CancellationToken, Task<IPreparedUpdate>> prepare) : IUpdateInstallerService
    {
        public Task<IPreparedUpdate> PrepareAsync(UpdatePackage package, IProgress<int>? progress, CancellationToken cancellationToken) =>
            prepare(package, progress, cancellationToken);
    }
    internal sealed class PreparedUpdate : IPreparedUpdate
    {
        public bool Committed { get; private set; }
        public bool Disposed { get; private set; }
        public void Commit() => Committed = true;
        public void Dispose() => Disposed = true;
    }
}
