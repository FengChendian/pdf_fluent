using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using pdf_fluent.Models;

namespace pdf_fluent.ViewModels;

public sealed partial class DocumentTabViewModel
{
    private readonly SemaphoreSlim _highlightSaveLock = new(1, 1);
    private readonly Dictionary<int, Task> _highlightLoadTasks = new();
    private readonly HashSet<int> _loadedHighlightPages = new();
    private readonly HashSet<int> _modifiedHighlightPages = new();
    private readonly Dictionary<int, List<PdfHighlight>> _savedHighlightPages = new();

    public ObservableCollection<PdfHighlight> Highlights { get; } = [];

    [ObservableProperty]
    private Color _highlightColor = HighlightPalette.Yellow;

    public Color? CustomHighlightColor { get; set; }

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
        // A multi-page selection uses the color chosen when the gesture finished.
        Color color = HighlightColor;

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
                color));
            UpdateHighlightModifiedState(pageIndex);
            added = true;
        }

        if (!added) return;
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
        UpdateHighlightModifiedState(highlight.PageIndex);
        HighlightErrorMessage = null;
        NotifyTextOverlaysChanged();
        return true;
    }

    private void UpdateHighlightModifiedState(int pageIndex)
    {
        List<PdfHighlight> current = Highlights
            .Where(highlight => highlight.PageIndex == pageIndex)
            .ToList();
        List<PdfHighlight> saved = _savedHighlightPages.TryGetValue(pageIndex, out List<PdfHighlight>? value)
            ? value
            : [];
        if (current.Count == saved.Count && current.All(saved.Contains))
            _modifiedHighlightPages.Remove(pageIndex);
        else
            _modifiedHighlightPages.Add(pageIndex);
        HasUnsavedHighlights = _modifiedHighlightPages.Count > 0;
    }

    public PdfHighlight? HitTestHighlight(int pageIndex, Point pagePoint, double toleranceInDips = 2)
    {
        PdfRect? pageBox = GetLoadedText(pageIndex)?.CropBox ?? GetLoadedLinks(pageIndex)?.CropBox;
        if (pageBox is not { } cropBox) return null;
        double scale = LayoutScale * Zoom;
        double x = cropBox.L + pagePoint.X / scale;
        double y = cropBox.T - pagePoint.Y / scale;
        double tolerance = toleranceInDips / scale;
        return Highlights.LastOrDefault(highlight =>
            highlight.PageIndex == pageIndex &&
            highlight.Rects.Any(rect => rect.Contains(x, y, tolerance, tolerance)));
    }

    public async Task<string> GetSelectedHighlightTextAsync()
    {
        if (SelectedHighlight is not { } highlight) return string.Empty;
        PdfTextPage text = await GetTextPageAsync(highlight.PageIndex);
        int start = -1;
        int end = -1;
        foreach (PdfTextCharacter character in text.Characters)
        {
            PdfRect bounds = character.LooseBounds;
            if (!highlight.Rects.Any(rect => rect.Contains(bounds.CenterX, bounds.CenterY))) continue;
            if (start < 0) start = character.Index;
            end = character.Index + 1;
        }
        return start < 0 ? string.Empty : text.GetText(start, end - start).TrimEnd('\r', '\n');
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
        _savedHighlightPages.Clear();
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
            _savedHighlightPages[page.PageIndex] = highlights;
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

    [RelayCommand]
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
            foreach (int pageIndex in modifiedPages)
            {
                _savedHighlightPages[pageIndex] = snapshot
                    .Where(highlight => highlight.PageIndex == pageIndex)
                    .ToList();
                UpdateHighlightModifiedState(pageIndex);
            }
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
