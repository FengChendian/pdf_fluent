using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using pdf_fluent.Models;
using pdf_fluent.Services;

namespace pdf_fluent.ViewModels;

public sealed partial class DocumentTabViewModel
{
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(220);
    private CancellationTokenSource? _searchCancellation;

    public event Action<PdfSearchResult>? SearchResultNavigationRequested;

    [ObservableProperty]
    private bool _isSearchPanelVisible;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private PdfSearchResult? _activeSearchResult;

    [ObservableProperty]
    private string? _searchErrorMessage;

    public ObservableCollection<PdfSearchResult> SearchResults { get; } = [];

    public string SearchStatus => IsSearching
        ? "正在搜索…"
        : SearchErrorMessage ?? (SearchResults.Count == 0
            ? string.IsNullOrWhiteSpace(SearchQuery) ? "输入关键词以搜索全文" : "未找到匹配内容"
            : $"找到 {SearchResults.Count} 处匹配");

    public void ShowSearch() => IsSearchPanelVisible = true;

    public void HideSearch() => IsSearchPanelVisible = false;

    partial void OnSearchQueryChanged(string value)
    {
        OnPropertyChanged(nameof(SearchStatus));
        RestartSearch();
    }

    partial void OnIsSearchingChanged(bool value) => OnPropertyChanged(nameof(SearchStatus));

    partial void OnSearchErrorMessageChanged(string? value) => OnPropertyChanged(nameof(SearchStatus));

    partial void OnIsSearchPanelVisibleChanged(bool value)
    {
        if (!value)
        {
            _searchCancellation?.Cancel();
            IsSearching = false;
        }
    }

    private void RestartSearch()
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        SearchResults.Clear();
        ActiveSearchResult = null;
        SearchErrorMessage = null;
        OnPropertyChanged(nameof(SearchStatus));
        if (!string.IsNullOrWhiteSpace(SearchQuery))
            _ = SearchAsync(SearchQuery.Trim(), _searchCancellation.Token);
        else
            IsSearching = false;
    }

    private async Task SearchAsync(string query, CancellationToken cancellationToken)
    {
        IsSearching = true;
        try
        {
            await Task.Delay(SearchDebounce, cancellationToken);
            for (int pageIndex = 0; pageIndex < Pages.Count; pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PdfTextPage page = await GetTextPageAsync(pageIndex);
                IReadOnlyList<PdfSearchResult> matches = await Task.Run(
                    () => PdfTextSearch.SearchPage(page, query), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                foreach (PdfSearchResult match in matches) SearchResults.Add(match);
                OnPropertyChanged(nameof(SearchStatus));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            SearchErrorMessage = $"搜索中断：{ex.Message}";
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested) IsSearching = false;
        }
    }

    public void GoToSearchResult(PdfSearchResult result)
    {
        ActiveSearchResult = result;
        CurrentPage = result.PageNumber;
        SetSelection(new PdfTextRange(
            new PdfTextPosition(result.PageIndex, result.CharacterIndex),
            new PdfTextPosition(result.PageIndex, result.CharacterIndex + result.CharacterCount)));
        SearchResultNavigationRequested?.Invoke(result);
    }
}
