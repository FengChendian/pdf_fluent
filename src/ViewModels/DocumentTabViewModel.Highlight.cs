using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using pdf_fluent.Models;

namespace pdf_fluent.ViewModels;

public sealed partial class DocumentTabViewModel
{
    private readonly SemaphoreSlim _highlightSaveLock = new(1, 1);
    private readonly Dictionary<int, Task> _highlightLoadTasks = new();
    private readonly HashSet<int> _loadedHighlightPages = new();
    private readonly HashSet<int> _modifiedHighlightPages = new();

    public ObservableCollection<PdfHighlight> Highlights { get; } = [];

    [ObservableProperty]
    private string? _highlightErrorMessage;

    [ObservableProperty]
    private bool _hasUnsavedHighlights;

    [ObservableProperty]
    private PdfHighlight? _selectedHighlight;

    public string DisplayFileName => HasUnsavedHighlights ? FileName + " *" : FileName;

    public IReadOnlyList<PdfHighlight> GetHighlights(int pageIndex) =>
        Highlights.Where(highlight => highlight.PageIndex == pageIndex).ToArray();

    public async Task AddHighlightAsync(PdfTextRange selection)
    {
        PdfTextRange range = selection.Normalize();
        if (range.IsEmpty) return;

        bool added = false;
        for (int pageIndex = range.Start.PageIndex; pageIndex <= range.End.PageIndex; pageIndex++)
        {
            if (pageIndex < 0 || pageIndex >= Pages.Count) continue;
            await EnsurePageHighlightsAsync(pageIndex);
            PdfTextPage text = await GetTextPageAsync(pageIndex);
            if (text.GetPageSelection(range) is not { } pageRange) continue;
            IReadOnlyList<PdfRect> rects = text.GetMergedRects(pageRange.Start, pageRange.Count);
            if (rects.Count == 0) continue;
            Highlights.Add(new PdfHighlight(
                pageIndex,
                rects,
                Color.FromArgb(96, 255, 235, 59)));
            _modifiedHighlightPages.Add(pageIndex);
            added = true;
        }

        if (!added) return;
        HasUnsavedHighlights = true;
        HighlightErrorMessage = null;
        ClearSelection();
        NotifyTextOverlaysChanged();
    }

    public async Task AddWordHighlightAsync(int pageIndex, int characterIndex)
    {
        PdfTextPage text = await GetTextPageAsync(pageIndex);
        (int start, int count) = text.GetWordRange(characterIndex);
        if (count == 0) return;
        await AddHighlightAsync(new PdfTextRange(
            new PdfTextPosition(pageIndex, start),
            new PdfTextPosition(pageIndex, start + count)));
    }

    public bool RemoveHighlight(PdfHighlight highlight)
    {
        if (!Highlights.Remove(highlight)) return false;
        if (ReferenceEquals(SelectedHighlight, highlight)) SelectedHighlight = null;
        _modifiedHighlightPages.Add(highlight.PageIndex);
        HasUnsavedHighlights = true;
        HighlightErrorMessage = null;
        NotifyTextOverlaysChanged();
        return true;
    }

    public PdfHighlight? HitTestHighlight(int pageIndex, Point pagePoint, double toleranceInDips = 2)
    {
        if (GetLoadedText(pageIndex) is not { } text) return null;
        double scale = LayoutScale * Zoom;
        double x = text.CropBox.L + pagePoint.X / scale;
        double y = text.CropBox.T - pagePoint.Y / scale;
        double tolerance = toleranceInDips / scale;
        return Highlights.LastOrDefault(highlight =>
            highlight.PageIndex == pageIndex &&
            highlight.Rects.Any(rect => rect.Contains(x, y, tolerance, tolerance)));
    }

    partial void OnHasUnsavedHighlightsChanged(bool value) => OnPropertyChanged(nameof(DisplayFileName));

    partial void OnSelectedHighlightChanged(PdfHighlight? value) => NotifyTextOverlaysChanged();

    private void ResetHighlights()
    {
        Highlights.Clear();
        SelectedHighlight = null;
        HasUnsavedHighlights = false;
        HighlightErrorMessage = null;
        _highlightLoadTasks.Clear();
        _loadedHighlightPages.Clear();
        _modifiedHighlightPages.Clear();
    }

    private Task EnsurePageHighlightsAsync(int pageIndex)
    {
        if (_loadedHighlightPages.Contains(pageIndex)) return Task.CompletedTask;
        if (_highlightLoadTasks.TryGetValue(pageIndex, out Task? pending)) return pending;

        PdfPageViewModel page = Pages[pageIndex];
        Task task = LoadAndPublishPageHighlightsAsync(page);
        _highlightLoadTasks[pageIndex] = task;
        return task;
    }

    private async Task LoadAndPublishPageHighlightsAsync(PdfPageViewModel page)
    {
        try
        {
            List<PdfHighlight> highlights = await Task.Run(() =>
                _pdfService.LoadHighlights(FilePath, page.PageIndex));
            if (!IsCurrentPage(page) || !_loadedHighlightPages.Add(page.PageIndex)) return;
            foreach (PdfHighlight highlight in highlights) Highlights.Add(highlight);
            NotifyTextOverlaysChanged();
        }
        finally
        {
            if (IsCurrentPage(page)) _highlightLoadTasks.Remove(page.PageIndex);
        }
    }

    private async Task ObserveHighlightLoadAsync(PdfPageViewModel page)
    {
        try
        {
            await EnsurePageHighlightsAsync(page.PageIndex);
        }
        catch (Exception ex) when (!_disposed)
        {
            HighlightErrorMessage = ex.Message;
        }
    }

    private bool IsCurrentPage(PdfPageViewModel page) =>
        !_disposed &&
        page.PageIndex >= 0 &&
        page.PageIndex < Pages.Count &&
        ReferenceEquals(Pages[page.PageIndex], page);

    public async Task<bool> SaveHighlightsAsync()
    {
        if (!HasUnsavedHighlights) return true;
        if (!File.Exists(FilePath))
        {
            HighlightErrorMessage = "高亮保存失败：PDF 文件不存在。";
            return false;
        }

        await _highlightSaveLock.WaitAsync();
        string tempPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            HashSet<int> modifiedPages = _modifiedHighlightPages.ToHashSet();
            PdfHighlight[] snapshot = Highlights
                .Where(highlight => modifiedPages.Contains(highlight.PageIndex))
                .ToArray();
            await Task.Run(() =>
                _pdfService.SaveHighlightsToTemp(FilePath, snapshot, tempPath, modifiedPages));
            _pdfService.ReleaseDocument(FilePath);
            File.Replace(tempPath, FilePath, null);
            _modifiedHighlightPages.ExceptWith(modifiedPages);
            HasUnsavedHighlights = _modifiedHighlightPages.Count > 0;
            HighlightErrorMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            HighlightErrorMessage = $"高亮保存失败：{ex.Message}";
            return false;
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            _highlightSaveLock.Release();
        }
    }
}
