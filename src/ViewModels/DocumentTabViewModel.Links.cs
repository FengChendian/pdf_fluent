using pdf_fluent.Models;

namespace pdf_fluent.ViewModels;

public sealed partial class DocumentTabViewModel
{
    private readonly Dictionary<int, LinkLoadRequest> _linkLoadTasks = new();
    public event Action<int>? PageLinksLoaded;

    public PdfLinkPage? GetLoadedLinks(int pageIndex) =>
        pageIndex >= 0 && pageIndex < Pages.Count ? Pages[pageIndex].Links : null;

    public async Task<PdfLinkPage> GetLinkPageAsync(int pageIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        if (pageIndex >= Pages.Count) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        while (true)
        {
            try { return await EnsurePageLinksAsync(Pages[pageIndex], automatic: false); }
            catch (OperationCanceledException) when (!_disposed) { }
        }
    }

    private Task<PdfLinkPage> EnsurePageLinksAsync(PdfPageViewModel page, bool automatic)
    {
        if (page.Links is { } loaded) return Task.FromResult(loaded);
        if (_linkLoadTasks.TryGetValue(page.PageIndex, out LinkLoadRequest? pending))
        {
            if (!automatic) pending.CancelWhenNotVisible = false;
            return pending.Task;
        }
        var request = new LinkLoadRequest(page, automatic);
        _linkLoadTasks[page.PageIndex] = request;
        request.Task = LoadAndPublishLinksAsync(request);
        return request.Task;
    }

    private async Task<PdfLinkPage> LoadAndPublishLinksAsync(LinkLoadRequest request)
    {
        PdfPageViewModel page = request.Page;
        try
        {
            PdfLinkPage links = await Task.Run(() => _pdfService.GetPageLinks(
                FilePath, page.PageIndex, request.Cancellation.Token), request.Cancellation.Token);
            if (!request.Cancellation.IsCancellationRequested && IsCurrentPage(page))
            {
                page.SetLinks(links);
                PageLinksLoaded?.Invoke(page.PageIndex);
            }
            return links;
        }
        finally
        {
            if (_linkLoadTasks.TryGetValue(page.PageIndex, out LinkLoadRequest? current) && ReferenceEquals(current, request))
                _linkLoadTasks.Remove(page.PageIndex);
            request.Dispose();
        }
    }

    private async Task ObserveVisiblePageContentAsync(PdfPageViewModel page)
    {
        while (IsCurrentPage(page) && _visiblePages.Contains(page) && page.Links is null)
        {
            try
            {
                // Publish links before bulk character extraction. Both calls reuse
                // PDFium's native text page, but search only calls GetPageText.
                await EnsurePageLinksAsync(page, automatic: true);
            }
            catch (OperationCanceledException) { }
            catch (Exception) { break; } // link failure must not disable text
        }
        if (IsCurrentPage(page) && _visiblePages.Contains(page)) await ObserveTextLoadAsync(page);
    }

    private void CancelAutomaticLinkLoadsExcept(IReadOnlySet<PdfPageViewModel> visiblePages)
    {
        foreach (LinkLoadRequest request in _linkLoadTasks.Values)
            if (request.CancelWhenNotVisible && !visiblePages.Contains(request.Page)) request.Cancellation.Cancel();
    }

    private sealed class LinkLoadRequest(PdfPageViewModel page, bool automatic) : IDisposable
    {
        public PdfPageViewModel Page { get; } = page;
        public Task<PdfLinkPage> Task { get; set; } = null!;
        public CancellationTokenSource Cancellation { get; } = new();
        public bool CancelWhenNotVisible { get; set; } = automatic;
        public void Dispose() => Cancellation.Dispose();
    }
}
