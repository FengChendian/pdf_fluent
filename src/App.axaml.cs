using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using System.Runtime.InteropServices;
using pdf_fluent.Diagnostics;
using pdf_fluent.ViewModels;
using pdf_fluent.Views;

namespace pdf_fluent;

public partial class App : Application
{
    public App()
    {
        StartupTrace.Mark("App.ctor");
    }

    public override void Initialize()
    {
        StartupTrace.Mark("App.Initialize.enter");
        AvaloniaXamlLoader.Load(this);
        StartupTrace.Mark("App.Initialize.exit (App.axaml + FluentTheme)");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        StartupTrace.Mark("FrameworkInit.enter");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainViewModel();
            StartupTrace.Mark("MainViewModel.ctor");
            var mainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
            desktop.MainWindow = mainWindow;
            StartupTrace.Mark("MainWindow.ctor");
            var openDocumentLock = new SemaphoreSlim(1, 1);

            // Let the shell paint its first frame before doing file I/O,
            // PDF parsing and page rendering. Opening a PDF from Explorer
            // passes the path as a command-line argument; doing that work
            // inside OnFrameworkInitializationCompleted delays the first
            // visible frame and makes the app look frozen on cold start.
            bool startupWorkStarted = false;
            mainWindow.Opened += (_, _) =>
            {
                if (startupWorkStarted) return;
                startupWorkStarted = true;
                StartupTrace.Mark("MainWindow.Opened -> start deferred startup work");
                _ = OpenDocumentsAsync(desktop.Args);
                _ = viewModel.CheckForUpdatesOnStartupAsync();
            };

            Program.SingleInstance?.StartListening(args => Dispatcher.UIThread.InvokeAsync(async () =>
            {
                ActivateMainWindow(desktop.MainWindow);
                await OpenDocumentsAsync(args);
            }));

            async Task OpenDocumentsAsync(string[]? args)
            {
                await openDocumentLock.WaitAsync();
                try
                {
                    await OpenStartupDocumentsAsync(viewModel, args);
                }
                finally
                {
                    openDocumentLock.Release();
                }
            }
        }

        base.OnFrameworkInitializationCompleted();
        StartupTrace.Mark("FrameworkInit.exit");
    }

    private static void ActivateMainWindow(Window? window)
    {
        if (window is null) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show();
        if (OperatingSystem.IsWindows() && window.TryGetPlatformHandle()?.Handle is nint handle && handle != 0)
            SetWindowPos(handle, 0, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        window.Activate();
    }

    internal static async Task OpenStartupDocumentsAsync(MainViewModel viewModel, string[]? args)
    {
        string[] filePaths = (args ?? [])
            .Where(path => string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (string filePath in filePaths)
            await viewModel.OpenDocumentAsync(filePath);
    }

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);
}