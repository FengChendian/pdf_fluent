using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using pdf_fluent.Models;

namespace pdf_fluent.ViewModels;

public sealed partial class DocumentTabViewModel
{
    private readonly List<TocEntryViewModel> _tocRoots = [];

    public ObservableCollection<TocEntryViewModel> VisibleTocEntries { get; } = [];
    public bool HasTableOfContents => _tocRoots.Count > 0;

    [ObservableProperty]
    private bool _isTableOfContentsOpen;

    private void SetTableOfContents(IEnumerable<TocItem> bookmarks)
    {
        ResetTableOfContents();
        foreach (TocItem bookmark in bookmarks)
            _tocRoots.Add(new TocEntryViewModel(bookmark, 0, RebuildVisibleTocEntries));

        OnPropertyChanged(nameof(HasTableOfContents));
        RebuildVisibleTocEntries();
    }

    private void ResetTableOfContents()
    {
        IsTableOfContentsOpen = false;
        VisibleTocEntries.Clear();
        _tocRoots.Clear();
        OnPropertyChanged(nameof(HasTableOfContents));
    }

    private void RebuildVisibleTocEntries(TocEntryViewModel? _ = null)
    {
        VisibleTocEntries.Clear();
        foreach (TocEntryViewModel root in _tocRoots) AddVisibleTocEntries(root);
    }

    private void AddVisibleTocEntries(TocEntryViewModel entry)
    {
        VisibleTocEntries.Add(entry);
        if (!entry.IsExpanded) return;

        foreach (TocEntryViewModel child in entry.Children) AddVisibleTocEntries(child);
    }
}
