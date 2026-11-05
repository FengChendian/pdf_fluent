using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using pdf_fluent.Models;

namespace pdf_fluent.ViewModels;

public sealed partial class DocumentTabViewModel
{
    private readonly Dictionary<int, TextLoadRequest> _textLoadTasks = new();
    private PdfTextRange? _selection;

    public event EventHandler? TextSelectionChanged;

    public PdfTextRange? Selection => _selection;
    public bool HasSelection => _selection is { IsEmpty: false };

    [ObservableProperty]
    private bool _isHighlightMode;

    [ObservableProperty]
    private double _highlightCornerRadius = 1.5;

    public PdfTextPage? GetLoadedText(int pageIndex) =>
        pageIndex >= 0 && pageIndex < Pages.Count ? Pages[pageIndex].Text : null;

    public async Task<PdfTextPage> GetTextPageAsync(int pageIndex)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DocumentTabViewModel));
        if (pageIndex < 0 || pageIndex >= Pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));

        while (true)
        {
            try
            {
                return await EnsurePageTextAsync(Pages[pageIndex], automatic: false);
            }
            catch (OperationCanceledException) when (!_disposed)
            {
            }
        }
    }

    private Task<PdfTextPage> EnsurePageTextAsync(PdfPageViewModel page, bool automatic)
    {
        if (page.Text is not null) return Task.FromResult(page.Text);
        if (_textLoadTasks.TryGetValue(page.PageIndex, out TextLoadRequest? pending))
        {
            if (!automatic) pending.CancelWhenNotVisible = false;
            return pending.Task;
        }

        var request = new TextLoadRequest(page, automatic);
        _textLoadTasks[page.PageIndex] = request;
        request.Task = LoadAndPublishTextAsync(request);
        return request.Task;
    }

    private async Task<PdfTextPage> LoadAndPublishTextAsync(TextLoadRequest request)
    {
        PdfPageViewModel page = request.Page;
        try
        {
            PdfTextPage text = await Task.Run(
                () => _pdfService.GetPageText(
                    FilePath, page.PageIndex, request.Cancellation.Token),
                request.Cancellation.Token);
            if (!_disposed && page.PageIndex < Pages.Count && ReferenceEquals(Pages[page.PageIndex], page))
                page.SetText(text);
            return text;
        }
        finally
        {
            if (!_disposed &&
                _textLoadTasks.TryGetValue(page.PageIndex, out TextLoadRequest? current) &&
                ReferenceEquals(current, request))
            {
                _textLoadTasks.Remove(page.PageIndex);
            }
            request.Dispose();
        }
    }

    private async Task ObserveTextLoadAsync(PdfPageViewModel page)
    {
        while (!_disposed && page.Text is null && _visiblePages.Contains(page))
        {
            try
            {
                await EnsurePageTextAsync(page, automatic: true);
                return;
            }
            catch (OperationCanceledException) when (!_disposed && _visiblePages.Contains(page))
            {
            }
            catch (Exception) when (!_disposed)
            {
                return;
            }
        }
    }

    private void CancelAutomaticTextLoadsExcept(IReadOnlySet<PdfPageViewModel> visiblePages)
    {
        foreach (TextLoadRequest request in _textLoadTasks.Values)
        {
            if (request.CancelWhenNotVisible && !visiblePages.Contains(request.Page))
                request.Cancellation.Cancel();
        }
    }

    private void CancelAutomaticTextLoad(PdfPageViewModel page)
    {
        if (_textLoadTasks.TryGetValue(page.PageIndex, out TextLoadRequest? request) &&
            request.CancelWhenNotVisible)
        {
            request.Cancellation.Cancel();
        }
    }

    private sealed class TextLoadRequest(PdfPageViewModel page, bool automatic) : IDisposable
    {
        public PdfPageViewModel Page { get; } = page;
        public Task<PdfTextPage> Task { get; set; } = null!;
        public CancellationTokenSource Cancellation { get; } = new();
        public bool CancelWhenNotVisible { get; set; } = automatic;
        public void Dispose() => Cancellation.Dispose();
    }

    public PdfTextHit? HitTestText(
        int pageIndex,
        Point pagePoint,
        bool nearestOnLine = false,
        double toleranceInDips = 1.5)
    {
        PdfTextPage? text = GetLoadedText(pageIndex);
        if (text is null) return null;
        double scale = LayoutScale * Zoom;
        double tolerance = toleranceInDips / scale;
        return text.HitTest(
            text.CropBox.L + pagePoint.X / scale,
            text.CropBox.T - pagePoint.Y / scale,
            nearestOnLine,
            tolerance,
            tolerance);
    }

    public void UpdateSelection(PdfTextPosition anchor, PdfTextPosition current)
    {
        SetSelection(new PdfTextRange(anchor, current).Normalize());
    }

    public void SetSelection(PdfTextRange? selection)
    {
        PdfTextRange? normalized = selection?.Normalize();
        if (normalized is { IsEmpty: true }) normalized = null;
        if (Nullable.Equals(_selection, normalized)) return;
        _selection = normalized;
        OnPropertyChanged(nameof(Selection));
        OnPropertyChanged(nameof(HasSelection));
        NotifyTextOverlaysChanged();
        TextSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearSelection() => SetSelection(null);

    public async Task<string> GetSelectedTextAsync()
    {
        if (_selection is not { } selection || selection.IsEmpty) return string.Empty;
        PdfTextRange range = selection.Normalize();
        var parts = new List<string>();
        for (int pageIndex = range.Start.PageIndex; pageIndex <= range.End.PageIndex; pageIndex++)
        {
            if (pageIndex < 0 || pageIndex >= Pages.Count) continue;
            PdfTextPage text = await GetTextPageAsync(pageIndex);
            if (text.GetPageSelection(range) is { } pageSelection)
                parts.Add(text.GetText(pageSelection.Start, pageSelection.Count).TrimEnd('\r', '\n'));
        }
        return string.Join(Environment.NewLine + Environment.NewLine, parts.Where(part => part.Length > 0));
    }

    public IReadOnlyList<PdfRect> GetSelectionRects(int pageIndex)
    {
        PdfTextPage? text = GetLoadedText(pageIndex);
        if (text is null || _selection is null) return [];
        return text.GetPageSelection(_selection.Value) is { } range
            ? text.GetMergedRects(range.Start, range.Count)
            : [];
    }

    public (int Start, int Count) GetWordRange(int pageIndex, int characterIndex) =>
        GetLoadedText(pageIndex)?.GetWordRange(characterIndex) ?? (0, 0);

    partial void OnIsHighlightModeChanged(bool value)
    {
        ClearSelection();
        NotifyTextOverlaysChanged();
    }

    private void NotifyTextOverlaysChanged()
    {
        foreach (PdfPageViewModel page in _realizedPages) page.NotifyTextOverlayChanged();
    }
}

internal static class PdfTextPageSelectionExtensions
{
    public static (int Start, int Count)? GetPageSelection(this PdfTextPage text, PdfTextRange range) =>
        range.GetPageRange(text.PageIndex, text.CharacterCount);
}
