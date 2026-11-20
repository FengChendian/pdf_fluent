using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using pdf_fluent.Services;

namespace pdf_fluent.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase, IDisposable
{
    private readonly IUpdateCheckService _updateCheckService;
    private readonly CancellationTokenSource _lifetimeSource = new();
    private string? _availableReleaseUrl;
    private bool _disposed;

    public SettingsViewModel() : this(new GitHubUpdateCheckService(), ApplicationVersion.Current)
    {
    }

    internal SettingsViewModel(
        IUpdateCheckService updateCheckService,
        Version currentVersion,
        Action<string>? releaseUrlOpener = null)
    {
        _updateCheckService = updateCheckService;
        CurrentVersion = currentVersion;
        ReleaseUrlOpener = releaseUrlOpener ?? OpenReleaseUrl;
        UpdateStatusMessage = "启动时自动检查，也可随时手动检查";
    }

    public Version CurrentVersion { get; }
    public string CurrentVersionText => $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{Math.Max(CurrentVersion.Build, 0)}";
    public string UpdateButtonText => IsCheckingUpdate ? "检查中" : HasAvailableUpdate ? "前往下载" : "检查更新";
    public bool HasAvailableUpdate => _availableReleaseUrl is not null;
    public Action<string> ReleaseUrlOpener { get; set; }

    [ObservableProperty]
    private bool _isCheckingUpdate;

    [ObservableProperty]
    private string? _latestVersion;

    [ObservableProperty]
    private string _updateStatusMessage = string.Empty;

    public Task CheckForUpdatesOnStartupAsync() => CheckForUpdateAsync();

    [RelayCommand]
    private async Task CheckForUpdateAsync()
    {
        if (IsCheckingUpdate || _disposed) return;

        IsCheckingUpdate = true;
        LatestVersion = null;
        _availableReleaseUrl = null;
        UpdateStatusMessage = "正在获取最新版本…";
        try
        {
            UpdateCheckResult result = await _updateCheckService.CheckForUpdateAsync(
                CurrentVersion, _lifetimeSource.Token);
            if (result.IsUpdateAvailable)
            {
                _availableReleaseUrl = result.ReleaseUrl;
                LatestVersion = result.LatestVersion;
                UpdateStatusMessage = $"发现新版本 {result.LatestVersion}";
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
            IsCheckingUpdate = false;
        }
    }

    private bool CanRunUpdateAction() => !IsCheckingUpdate;

    [RelayCommand(CanExecute = nameof(CanRunUpdateAction))]
    private async Task UpdateActionAsync()
    {
        if (HasAvailableUpdate) DownloadUpdate();
        else await CheckForUpdateAsync();
    }

    [RelayCommand]
    private void DownloadUpdate()
    {
        if (_availableReleaseUrl is null || IsCheckingUpdate) return;
        try
        {
            ReleaseUrlOpener(_availableReleaseUrl);
        }
        catch (Exception)
        {
            UpdateStatusMessage = "无法打开下载页面，请稍后重试";
        }
    }

    partial void OnIsCheckingUpdateChanged(bool value)
    {
        OnPropertyChanged(nameof(UpdateButtonText));
        UpdateActionCommand.NotifyCanExecuteChanged();
    }

    partial void OnLatestVersionChanged(string? value)
    {
        OnPropertyChanged(nameof(HasAvailableUpdate));
        OnPropertyChanged(nameof(UpdateButtonText));
        UpdateActionCommand.NotifyCanExecuteChanged();
    }

    private static void OpenReleaseUrl(string url) => Process.Start(new ProcessStartInfo
    {
        FileName = url,
        UseShellExecute = true,
    });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetimeSource.Cancel();
        _lifetimeSource.Dispose();
        (_updateCheckService as IDisposable)?.Dispose();
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
