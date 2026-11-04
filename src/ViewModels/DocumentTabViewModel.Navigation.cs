using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace pdf_fluent.ViewModels;

public sealed partial class DocumentTabViewModel
{
    [ObservableProperty]
    private int _currentPage = 1;

    [ObservableProperty]
    private bool _isFavorite;

    public Vector ScrollOffset { get; private set; }

    public string PageIndicator => $"{CurrentPage} / {PageCount}";
    public string FloatingPageIndicator => $"第 {CurrentPage} 页 / 共 {PageCount} 页";
    public bool CanGoToPreviousPage => PageCount > 0 && CurrentPage > 1;
    public bool CanGoToNextPage => PageCount > 0 && CurrentPage < PageCount;
    public string FavoriteToolTip => IsFavorite ? "取消收藏" : "收藏文档";

    public void UpdateScrollOffset(Vector offset)
    {
        if (!double.IsFinite(offset.X) || !double.IsFinite(offset.Y)) return;
        ScrollOffset = new Vector(Math.Max(0, offset.X), Math.Max(0, offset.Y));
    }

    internal void UpdateCurrentPage(IReadOnlyCollection<PdfPageViewModel> visiblePages)
    {
        if (PageCount == 0) return;
        int pageNumber = visiblePages.Count == 0 ? 1 : visiblePages.Min(page => page.PageIndex) + 1;
        CurrentPage = Math.Clamp(pageNumber, 1, PageCount);
    }

    partial void OnPageCountChanged(int value)
    {
        CurrentPage = value == 0 ? 1 : Math.Clamp(CurrentPage, 1, value);
        OnPropertyChanged(nameof(PageIndicator));
        OnPropertyChanged(nameof(FloatingPageIndicator));
        OnPropertyChanged(nameof(CanGoToPreviousPage));
        OnPropertyChanged(nameof(CanGoToNextPage));
    }

    partial void OnCurrentPageChanged(int value)
    {
        OnPropertyChanged(nameof(PageIndicator));
        OnPropertyChanged(nameof(FloatingPageIndicator));
        OnPropertyChanged(nameof(CanGoToPreviousPage));
        OnPropertyChanged(nameof(CanGoToNextPage));
    }

    partial void OnIsFavoriteChanged(bool value) => OnPropertyChanged(nameof(FavoriteToolTip));
}
