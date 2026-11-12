using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using pdf_fluent.ViewModels;

namespace pdf_fluent.Views;

public partial class MainWindow : Window
{
    private bool _closeConfirmed;
    private bool _isConfirmingClose;

    public MainWindow()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel viewModel)
            {
                viewModel.PdfFilePicker = PickPdfFileAsync;
                viewModel.DocumentCloseConfirmation = ConfirmDocumentCloseAsync;
            }
        };
        Closing += OnWindowClosing;
        Closed += (_, _) => (DataContext as MainViewModel)?.Dispose();
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = GetPdfFilePaths(e.DataTransfer).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        string[] filePaths = GetPdfFilePaths(e.DataTransfer);
        e.DragEffects = filePaths.Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;

        if (DataContext is not MainViewModel viewModel) return;
        foreach (string filePath in filePaths)
            await viewModel.OpenDocumentAsync(filePath);
    }

    private static string[] GetPdfFilePaths(IDataTransfer data) =>
        (data.TryGetFiles() ?? [])
            .OfType<IStorageFile>()
            .Select(file => file.TryGetLocalPath())
            .Where(path => string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
            .Select(path => path!)
            .ToArray();

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed) return;
        e.Cancel = true;
        if (_isConfirmingClose) return;

        if (DataContext is not MainViewModel viewModel)
        {
            _closeConfirmed = true;
            Close();
            return;
        }

        _isConfirmingClose = true;
        try
        {
            if (await viewModel.ConfirmCloseAllDocumentsAsync())
            {
                _closeConfirmed = true;
                Close();
            }
        }
        finally
        {
            _isConfirmingClose = false;
        }
    }

    private async Task<DocumentCloseDecision> ConfirmDocumentCloseAsync(DocumentTabViewModel document)
    {
        var dialog = new Window
        {
            Title = "保存高亮",
            Width = 430,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.White,
        };
        var saveButton = new Button
        {
            Content = "保存",
            Padding = new Avalonia.Thickness(18, 8),
            Background = new SolidColorBrush(Color.Parse("#6C5CE7")),
            Foreground = Brushes.White,
        };
        var discardButton = new Button { Content = "不保存", Padding = new Avalonia.Thickness(18, 8) };
        var cancelButton = new Button { Content = "取消", Padding = new Avalonia.Thickness(18, 8) };
        saveButton.Click += (_, _) => dialog.Close(DocumentCloseDecision.Save);
        discardButton.Click += (_, _) => dialog.Close(DocumentCloseDecision.Discard);
        cancelButton.Click += (_, _) => dialog.Close(DocumentCloseDecision.Cancel);

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 18,
            Children =
            {
                new TextBlock
                {
                    Text = $"是否保存对“{document.FileName}”的高亮修改？",
                    FontSize = 16,
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = "选择“不保存”将放弃本次新增和删除的高亮。",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.Parse("#71778A")),
                    TextWrapping = TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { saveButton, discardButton, cancelButton },
                },
            },
        };
        DocumentCloseDecision? result = await dialog.ShowDialog<DocumentCloseDecision?>(this);
        return result ?? DocumentCloseDecision.Cancel;
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
