using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using pdf_fluent.Services;
using Xunit;

namespace pdf_fluent.Tests;

public sealed class UpdateInstallerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadRequiresTheManifestHashBeforePublishingACompleteFile(bool corrupt)
    {
        byte[] bytes = [0x4d, 0x5a, 1, 2, 3];
        var package = UpdateTestData.Package() with
        {
            Size = bytes.Length,
            Sha256 = corrupt ? UpdateTestData.Hash : Convert.ToHexString(SHA256.HashData(bytes)),
        };
        using var client = new HttpClient(new UpdateTestData.HttpHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })));
        using var service = new WindowsUpdateInstallerService(client);
        string path = Path.Combine(Path.GetTempPath(), $"fluent-update-test-{Guid.NewGuid():N}.exe");
        try
        {
            if (corrupt)
            {
                await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadInstallerAsync(
                    package, path, null, TestContext.Current.CancellationToken));
                Assert.False(File.Exists(path));
            }
            else
            {
                await service.DownloadInstallerAsync(package, path, null, TestContext.Current.CancellationToken);
                Assert.Equal(bytes, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task TruncatedDownloadIsRemovedWithoutStartingAnInstaller()
    {
        using var client = new HttpClient(new UpdateTestData.HttpHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0x4d, 0x5a]) })));
        using var service = new WindowsUpdateInstallerService(client);
        string path = Path.Combine(Path.GetTempPath(), $"fluent-update-test-{Guid.NewGuid():N}.exe");
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadInstallerAsync(
                UpdateTestData.Package() with { Size = 5 }, path, null, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(path));
        }
        finally { File.Delete(path); }
    }
}
