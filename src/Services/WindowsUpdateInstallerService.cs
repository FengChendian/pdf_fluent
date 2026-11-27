using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;

namespace pdf_fluent.Services;

public interface IUpdateInstallerService
{
    Task<IPreparedUpdate> PrepareAsync(UpdatePackage package, IProgress<int>? progress, CancellationToken cancellationToken);
}

public interface IPreparedUpdate : IDisposable
{
    // Call only after unsaved documents have been handled, immediately before closing.
    void Commit();
}

public sealed class WindowsUpdateInstallerService : IUpdateInstallerService, IDisposable
{
    private readonly HttpClient _httpClient;

    public WindowsUpdateInstallerService() : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }))
    {
    }

    internal WindowsUpdateInstallerService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Fluent-PDF-Updater");
    }

    public async Task<IPreparedUpdate> PrepareAsync(
        UpdatePackage package, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() ||
            !string.Equals(Path.GetFileName(Environment.ProcessPath), "pdf_fluent.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请从已安装的 Windows 应用中执行更新。");

        string directory = Path.Combine(Path.GetTempPath(), "FluentPDF-Updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Process? helper = null;
        try
        {
            string installerPath = Path.Combine(directory, "installer.exe");
            await DownloadInstallerAsync(package, installerPath, progress, cancellationToken).ConfigureAwait(false);
            string scriptPath = Path.Combine(directory, "install.ps1");
            await using (Stream script = typeof(WindowsUpdateInstallerService).Assembly.GetManifestResourceStream(
                             "pdf_fluent.Services.InstallUpdate.ps1") ?? throw new InvalidOperationException("找不到更新程序。"))
            await using (FileStream output = File.Create(scriptPath))
                await script.CopyToAsync(output, cancellationToken).ConfigureAwait(false);

            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    @"WindowsPowerShell\v1.0\powershell.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = directory,
            };
            foreach (string argument in new[]
                     {
                         "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden",
                         "-File", scriptPath,
                         "-ApplicationPath", Environment.ProcessPath!, "-ParentProcessId", Environment.ProcessId.ToString(),
                         "-ExpectedHash", package.Sha256,
                     })
                startInfo.ArgumentList.Add(argument);
            helper = Process.Start(startInfo) ?? throw new IOException("无法启动更新程序。");

            // Do not close the reader unless the independent helper is actually ready.
            using var readyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readyTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            while (!File.Exists(Path.Combine(directory, "ready")))
            {
                if (helper.HasExited) throw new IOException("更新程序未能启动。");
                await Task.Delay(100, readyTimeout.Token).ConfigureAwait(false);
            }
            return new PreparedUpdate(directory, helper);
        }
        catch
        {
            StopHelper(helper);
            Cleanup(directory);
            throw;
        }
    }

    internal async Task DownloadInstallerAsync(
        UpdatePackage package, string destination, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        package.Validate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(
                package.DownloadUri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long contentLength && contentLength != package.Size)
                throw new InvalidDataException("安装包大小与更新清单不一致。");

            await using (Stream input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[81920];
                long total = 0;
                int previousPercent = -1;
                while (true)
                {
                    int count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    total += count;
                    if (total > package.Size) throw new InvalidDataException("安装包超过预期大小。");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                    int percent = (int)Math.Min(99, total * 100 / package.Size);
                    if (percent != previousPercent)
                    {
                        progress?.Report(percent);
                        previousPercent = percent;
                    }
                }
                if (total != package.Size || !CryptographicOperations.FixedTimeEquals(
                        hash.GetHashAndReset(), Convert.FromHexString(package.Sha256)))
                    throw new InvalidDataException("安装包校验失败，请重新下载。");
            }
            progress?.Report(100);
        }
        catch
        {
            TryDeleteFile(destination);
            throw;
        }
    }

    private sealed class PreparedUpdate(string directory, Process helper) : IPreparedUpdate
    {
        private bool _committed;

        public void Commit()
        {
            if (helper.HasExited) throw new IOException("更新程序已退出，请重试。");
            File.WriteAllText(Path.Combine(directory, "commit"), string.Empty);
            _committed = true;
        }

        public void Dispose()
        {
            if (_committed) helper.Dispose(); // The helper now owns the files and installation.
            else
            {
                StopHelper(helper);
                Cleanup(directory);
            }
        }
    }

    private static void StopHelper(Process? helper)
    {
        if (helper is null) return;
        try
        {
            if (!helper.HasExited)
            {
                helper.Kill();
                helper.WaitForExit(3000);
            }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        finally { helper.Dispose(); }
    }

    private static void Cleanup(string directory)
    {
        // Only files created by this attempt; never recursively delete a user directory.
        foreach (string name in new[] { "installer.exe", "install.ps1", "ready", "commit" })
            TryDeleteFile(Path.Combine(directory, name));
        try { Directory.Delete(directory); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose() => _httpClient.Dispose();
}
