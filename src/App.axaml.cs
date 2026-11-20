using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using pdf_fluent.ViewModels;
using pdf_fluent.Views;

namespace pdf_fluent;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainViewModel();
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
            _ = viewModel.CheckForUpdatesOnStartupAsync();
            var openDocumentLock = new SemaphoreSlim(1, 1);
            _ = OpenDocumentsAsync(desktop.Args);
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
    }

    private static void ActivateMainWindow(Window? window)
    {
        if (window is null) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show();
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
}