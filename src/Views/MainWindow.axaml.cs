using Avalonia.Controls;
using Avalonia.Platform.Storage;
using pdf_fluent.ViewModels;

namespace pdf_fluent.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel viewModel)
                viewModel.PdfFilePicker = PickPdfFileAsync;
        };
        Closed += (_, _) => (DataContext as MainViewModel)?.Dispose();
    }

    private async Task<string?> PickPdfFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开 PDF 文件",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("PDF 文档") { Patterns = ["*.pdf"] },
            ],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
