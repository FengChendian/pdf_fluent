using System.Windows.Input;
using Avalonia.Media.Imaging;
using pdf_fluent.Models;

namespace pdf_fluent.ViewModels;

public sealed class RecentDocumentViewModel(RecentDocument document, ICommand? removeCommand = null) : ViewModelBase, IDisposable
{
    private WriteableBitmap? _thumbnail;

    public ICommand? RemoveCommand { get; } = removeCommand;

    public string FilePath { get; } = document.FilePath;
    public string FileName => Path.GetFileNameWithoutExtension(FilePath);
    public int PageCount { get; } = document.PageCount;
    public DateTimeOffset LastOpened { get; } = document.LastOpened;
    public string PageCountText => $"{PageCount} 页";
    public string LastOpenedText => LastOpened.ToLocalTime().ToString("MM月dd日 HH:mm");

    public WriteableBitmap? Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }

    public RecentDocument ToModel() => new()
    {
        FilePath = FilePath,
        PageCount = PageCount,
        LastOpened = LastOpened,
    };

    public void Dispose() => _thumbnail?.Dispose();
}
