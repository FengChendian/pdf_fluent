using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using pdf_fluent.Services;

namespace pdf_fluent.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase, IDisposable
{
    private readonly IUpdateCheckService _updateCheckService;
    private readonly IUpdateInstallerService _updateInstallerService;
    private readonly CancellationTokenSource _lifetimeSource = new();
    private UpdatePackage? _availablePackage;
    private bool _disposed;

    public SettingsViewModel() : this(new R2UpdateCheckService(), ApplicationVersion.Current)
    {
    }

    internal SettingsViewModel(
        IUpdateCheckService updateCheckService,
        Version currentVersion,
        IUpdateInstallerService? updateInstallerService = null)
    {
        _updateCheckService = updateCheckService;
        CurrentVersion = currentVersion;
        _updateInstallerService = updateInstallerService ?? new WindowsUpdateInstallerService();
        UpdateStatusMessage = "启动时自动检查，也可随时手动检查";
    }

    public Version CurrentVersion { get; }
    public string CurrentVersionText => $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{Math.Max(CurrentVersion.Build, 0)}";
    public string UpdateButtonText => IsCheckingUpdate ? "检查中"
        : IsInstallingUpdate ? DownloadProgress < 100 ? $"下载中 {DownloadProgress}%" : "准备安装"
        : HasAvailableUpdate ? "下载并更新" : "检查更新";
    public bool HasAvailableUpdate => _availablePackage is not null;
    public bool IsBusy => IsCheckingUpdate || IsInstallingUpdate;
    // The window confirms unsaved changes, invokes the commit action, then closes.
    public Func<Action, Task<bool>>? CloseForUpdateAsync { get; set; }

    [ObservableProperty]
    private bool _isCheckingUpdate;

    [ObservableProperty]
    private bool _isInstallingUpdate;

    [ObservableProperty]
    private int _downloadProgress;

    [ObservableProperty]
    private string? _latestVersion;

    [ObservableProperty]
    private string _updateStatusMessage = string.Empty;

    public Task CheckForUpdatesOnStartupAsync() => CheckForUpdateAsync();

    [RelayCommand(CanExecute = nameof(CanRunUpdateAction))]
    private async Task CheckForUpdateAsync()
    {
        if (IsBusy || _disposed) return;

        IsCheckingUpdate = true;
        LatestVersion = null;
        _availablePackage = null;
        NotifyAvailableUpdateChanged();
        UpdateStatusMessage = "正在获取最新版本…";
        try
        {
            UpdateCheckResult result = await _updateCheckService.CheckForUpdateAsync(
                CurrentVersion, _lifetimeSource.Token);
            if (_disposed) return;
            if (result.IsUpdateAvailable)
            {
                _availablePackage = result.Package;
                LatestVersion = result.LatestVersion;
                UpdateStatusMessage = $"发现新版本 {result.LatestVersion}";
                NotifyAvailableUpdateChanged();
            }
            else
            {
                UpdateStatusMessage = "当前已是最新版本";
            }
        }
        catch (OperationCanceledException)
        {
            if (!_disposed) UpdateStatusMessage = "检查超时，请稍后重试";
        }
        catch (Exception)
        {
            if (!_disposed) UpdateStatusMessage = "检查失败，请检查网络后重试";
        }
        finally
        {
            if (!_disposed) IsCheckingUpdate = false;
        }
    }

    private bool CanRunUpdateAction() => !IsBusy && !_disposed;

    [RelayCommand(CanExecute = nameof(CanRunUpdateAction))]
    private async Task UpdateActionAsync()
    {
        if (HasAvailableUpdate) await DownloadUpdateAsync();
        else await CheckForUpdateAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRunUpdateAction))]
    private async Task DownloadUpdateAsync()
    {
        if (_availablePackage is null || IsBusy || _disposed) return;
        if (CloseForUpdateAsync is null)
        {
            UpdateStatusMessage = "无法启动更新，请重新打开应用后重试";
            return;
        }
        IsInstallingUpdate = true;
        DownloadProgress = 0;
        UpdateStatusMessage = "正在下载安装包，完成后将重启应用";
        try
        {
            var progress = new Progress<int>(percent =>
            {
                if (!_disposed && IsInstallingUpdate && percent > DownloadProgress) DownloadProgress = percent;
            });
            using IPreparedUpdate prepared = await _updateInstallerService.PrepareAsync(
                _availablePackage, progress, _lifetimeSource.Token);
            if (_disposed) return;
            DownloadProgress = 100;
            UpdateStatusMessage = "安装包已就绪，正在准备关闭应用";
            if (!await CloseForUpdateAsync(prepared.Commit) && !_disposed)
                UpdateStatusMessage = "已取消更新，应用将保持打开";
        }
        catch (OperationCanceledException)
        {
            if (!_disposed) UpdateStatusMessage = "下载超时，请稍后重试";
        }
        catch (InvalidDataException)
        {
            if (!_disposed) UpdateStatusMessage = "安装包校验失败，请重新下载";
        }
        catch (System.Net.Http.HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            if (!_disposed)
            {
                _availablePackage = null;
                LatestVersion = null;
                NotifyAvailableUpdateChanged();
                UpdateStatusMessage = "下载版本已变更，请重新检查更新";
            }
        }
        catch (Exception)
        {
            if (!_disposed) UpdateStatusMessage = "更新失败，请检查网络后重试";
        }
        finally
        {
            if (!_disposed) IsInstallingUpdate = false;
        }
    }

    partial void OnIsCheckingUpdateChanged(bool value)
    {
        NotifyBusyChanged();
    }

    partial void OnIsInstallingUpdateChanged(bool value) => NotifyBusyChanged();

    partial void OnDownloadProgressChanged(int value) => OnPropertyChanged(nameof(UpdateButtonText));

    private void NotifyBusyChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(UpdateButtonText));
        UpdateActionCommand.NotifyCanExecuteChanged();
        DownloadUpdateCommand.NotifyCanExecuteChanged();
        CheckForUpdateCommand.NotifyCanExecuteChanged();
    }

    private void NotifyAvailableUpdateChanged()
    {
        OnPropertyChanged(nameof(HasAvailableUpdate));
        OnPropertyChanged(nameof(UpdateButtonText));
        UpdateActionCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetimeSource.Cancel();
        _lifetimeSource.Dispose();
        (_updateCheckService as IDisposable)?.Dispose();
        (_updateInstallerService as IDisposable)?.Dispose();
    }
}

internal static class ApplicationVersion
{
    public static Version Current { get; } = GetCurrentVersion();

    private static Version GetCurrentVersion()
    {
        string? informationalVersion = typeof(App).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (informationalVersion is not null)
        {
            string version = informationalVersion.Split('+', 2)[0];
            if (Version.TryParse(version, out Version? parsed)) return parsed;
        }
        return typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0);
    }
}
