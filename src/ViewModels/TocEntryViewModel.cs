using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using pdf_fluent.Models;

namespace pdf_fluent.ViewModels;

public sealed partial class TocEntryViewModel : ViewModelBase
{
    private readonly Action<TocEntryViewModel> _expandedChanged;

    internal TocEntryViewModel(
        TocItem item,
        int depth,
        Action<TocEntryViewModel> expandedChanged)
    {
        Title = string.IsNullOrWhiteSpace(item.Title) ? "未命名章节" : item.Title;
        PageIndex = item.PageIndex;
        Depth = depth;
        Children = item.Children
            .Select(child => new TocEntryViewModel(child, depth + 1, expandedChanged))
            .ToArray();
        _expandedChanged = expandedChanged;
    }

    public string Title { get; }
    public int PageIndex { get; }
    public int Depth { get; }
    public IReadOnlyList<TocEntryViewModel> Children { get; }
    public bool HasChildren => Children.Count > 0;
    public bool CanNavigate => PageIndex >= 0;
    public Thickness Indent => new(Depth * 18, 0, 0, 0);

    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value) => _expandedChanged(this);
}
