using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
            _ = OpenStartupDocumentsAsync(viewModel, desktop.Args);
        }

        base.OnFrameworkInitializationCompleted();
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