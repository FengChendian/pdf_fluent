using System.Windows.Input;
using Avalonia.Media.Imaging;
using pdf_fluent.Models;

namespace pdf_fluent.ViewModels;

public sealed class RecentDocumentViewModel(
    RecentDocument document,
    ICommand? removeCommand = null,
    string removeActionText = "删除最近记录") : ViewModelBase, IDisposable
{
    private byte[]? _thumbnailData = document.Thumbnail;
    private Bitmap? _thumbnail = DecodeThumbnail(document.Thumbnail);

    public ICommand? RemoveCommand { get; } = removeCommand;
    public string RemoveActionText { get; } = removeActionText;

    public string FilePath { get; } = document.FilePath;
    public string FileName => Path.GetFileNameWithoutExtension(FilePath);
    public int PageCount { get; } = document.PageCount;
    public DateTimeOffset LastOpened { get; } = document.LastOpened;
    public Avalonia.Vector ScrollOffset { get; private set; } = new(
        document.ScrollOffsetX,
        Math.Max(0, document.ScrollOffsetY - (document.ScrollLayoutVersion == 0 ? 48 : 0)));
    public bool IsUnread { get; private set; } = document.IsUnread;
    public string PageCountText => $"{PageCount} 页";
    public string LastOpenedText => LastOpened.ToLocalTime().ToString("MM月dd日 HH:mm");

    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }

    public void MarkAsRead() => IsUnread = false;

    public void UpdateScrollOffset(Avalonia.Vector offset) => ScrollOffset = offset;

    public void UpdateThumbnail(byte[]? data)
    {
        _thumbnail?.Dispose();
        _thumbnailData = data;
        Thumbnail = DecodeThumbnail(data);
    }

    public RecentDocument ToModel() => new()
    {
        FilePath = FilePath,
        PageCount = PageCount,
        LastOpened = LastOpened,
        ScrollOffsetX = ScrollOffset.X,
        ScrollOffsetY = ScrollOffset.Y,
        ScrollLayoutVersion = 1,
        IsUnread = IsUnread,
        Thumbnail = _thumbnailData,
    };

    private static Bitmap? DecodeThumbnail(byte[]? data)
    {
        if (data is null || data.Length == 0) return null;

        try
        {
            using var stream = new MemoryStream(data, writable: false);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose() => _thumbnail?.Dispose();
}
