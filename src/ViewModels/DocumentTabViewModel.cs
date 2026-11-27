using System.Collections.ObjectModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using pdf_fluent.Diagnostics;
using pdf_fluent.Models;
using pdf_fluent.Services;

namespace pdf_fluent.ViewModels;

public sealed partial class DocumentTabViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// Layout units (device independent pixels) per PDF point at 100% zoom.
    /// Bitmaps are rendered with <c>LayoutScale * zoom * RenderScaling</c> so that
    /// one bitmap pixel always maps to one physical pixel on screen at the
    /// current zoom, which is what keeps the image sharp on HiDPI displays.
    /// </summary>
    public const double LayoutScale = 1.25;

    public const double MinZoom = 0.5;
    public const double MaxZoom = 2.5;

    /// <summary>
    /// Upper bound for the bitmap resolution in device pixels per PDF point
    /// (~360 DPI).  Beyond this the bitmap is upscaled instead of re-rendered:
    /// it bounds both memory (pixel count grows quadratically) and the work
    /// done on the render thread.  A 200% screen reaches this bound at 200%
    /// zoom, a 100% screen only above the maximum zoom.
    /// </summary>
    public const double MaxRenderScale = 5.0;

    /// <summary>
    /// Upper bound for a single bitmap edge.  Large pages at high zoom would
    /// otherwise exceed the maximum texture size of the GPU backend.
    /// </summary>
    public const double MaxBitmapEdgePixels = 8192;

    /// <summary>Render-scale differences below this are treated as "already rendered".</summary>
    private const double RenderScaleTolerance = 0.005;

    /// <summary>
    /// Bitmap bytes the LRU cache may retain for evictable pages. Pages the
    /// panel still keeps alive (visible plus its prefetch buffer) are never
    /// evicted, so the live total can exceed this value briefly, by at most
    /// those pages. The budget is expressed in real bitmap bytes, which is
    /// why a page re-rendered at a different zoom or screen scaling has to
    /// update its cache entry (see <see cref="_cacheEntryBytes"/>).
    /// </summary>
    private const long MaxBitmapCacheBytes = 256 * 1024 * 1024;
    private const int MaxQueuedRenderRequests = 32;
    private readonly IPdfDocumentService _pdfService;
    private readonly HashSet<PdfPageViewModel> _realizedPages = new();
    private readonly HashSet<PdfPageViewModel> _visiblePages = new();
    private readonly LinkedList<RenderRequest> _visibleRenderQueue = new();
    private readonly LinkedList<RenderRequest> _prefetchRenderQueue = new();
    private readonly Dictionary<int, RenderRequest> _renderRequests = new();
    private readonly LinkedList<PdfPageViewModel> _cacheLru = new();
    private readonly Dictionary<int, LinkedListNode<PdfPageViewModel>> _cacheNodes = new();

    /// <summary>
    /// Bitmap bytes each cache entry was accounted with when it was inserted.
    /// The page's bitmap can be replaced by a re-render at a different zoom or
    /// screen scaling, so the size cannot be read back from the page when the
    /// entry is removed — that would subtract the new size instead of the size
    /// that was actually added and make the byte budget drift.
    /// </summary>
    private readonly Dictionary<int, long> _cacheEntryBytes = new();
    private readonly SemaphoreSlim _renderSignal = new(0);
    private readonly CancellationTokenSource _workerCancellation = new();
    private bool _loadInProgress;
    private bool _disposed;
    private bool _renderingSuspended;
    private int _scrollDirection = 1;
    private int _renderVersion;
    private long _cacheBytes;
    private double _zoom = 1;
    private double? _visualZoom;
    private double _renderZoom = 1;
    private double _renderScaling = 1;
    private double _appliedScaling = 1;

    public DocumentTabViewModel(
        string filePath,
        IPdfDocumentService pdfService,
        Avalonia.Vector? scrollOffset = null)
    {
        FilePath = filePath;
        _pdfService = pdfService;
        UpdateScrollOffset(scrollOffset ?? default);
        _ = RenderWorkerAsync();
    }

    public string FilePath { get; }
    public string FileName => Path.GetFileNameWithoutExtension(FilePath);
    public double VisualZoom => _visualZoom ?? _zoom;
    public bool IsZoomAnimating => _visualZoom.HasValue;
    public int ZoomPercent => (int)Math.Round(VisualZoom * 100);
    public bool IsLoaded => !IsLoading;
    internal int PendingRenderCount => _renderRequests.Count;
    internal int QueuedRenderCount => _visibleRenderQueue.Count + _prefetchRenderQueue.Count;
    internal bool IsRenderingSuspended => _renderingSuspended;

    /// <summary>Bitmap bytes the cache currently accounts for.</summary>
    internal long CachedBitmapBytes => _cacheBytes;

    /// <summary>Number of pages whose bitmap is held by the cache.</summary>
    internal int CachedPageCount => _cacheNodes.Count;

    /// <summary>Pages the view model currently treats as realized by the panel.</summary>
    internal int RealizedPageCount => _realizedPages.Count;

    /// <summary>
    /// Layout zoom. Changing it updates the document geometry and realized page
    /// containers without discarding bitmaps: pages keep showing their previous
    /// render until a sharper render is needed and arrives.
    /// </summary>
    public double Zoom
    {
        get => _zoom;
        set
        {
            if (double.IsNaN(value)) return;

            double clamped = Math.Clamp(value, MinZoom, MaxZoom);
            if (!SetProperty(ref _zoom, clamped)) return;

            OnPropertyChanged(nameof(VisualZoom));
            OnPropertyChanged(nameof(ZoomPercent));
            ApplyLayoutZoom();
        }
    }

    /// <summary>
    /// Physical pixels per device independent pixel of the screen the document
    /// is shown on (<see cref="Avalonia.Controls.TopLevel.RenderScaling"/>).
    /// Bitmaps are rendered at <c>LayoutScale * zoom * RenderScaling</c> device
    /// pixels per PDF point, so a page is pixel-exact on a HiDPI screen; the
    /// view updates this value when the window moves to another monitor.
    /// </summary>
    public double RenderScaling
    {
        get => _renderScaling;
        set
        {
            if (double.IsNaN(value)) return;

            double clamped = Math.Clamp(value, 0.5, 5.0);
            if (!SetProperty(ref _renderScaling, clamped)) return;

            CommitRenderState();
        }
    }

    [ObservableProperty]
    private ObservableCollection<PdfPageViewModel> _pages = [];

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private int _pageCount;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsLoaded));

    partial void OnIsSelectedChanged(bool value)
    {
        // Hidden tabs stop doing work, but keep their bitmaps so returning to
        // the tab can display the cached pages without rendering them again.
        if (!value) StopRendering();
    }

    public async Task LoadAsync()
    {
        if (_loadInProgress || _disposed) return;

        _loadInProgress = true;
        IsLoading = true;
        ErrorMessage = null;
        ClearPages();
        ResetHighlights();
        ResetTableOfContents();
        StartupTrace.Mark($"DocumentTabViewModel.LoadAsync.enter ({Path.GetFileName(FilePath)})");

        try
        {
            var (sizes, bookmarks) = await Task.Run(() =>
                (_pdfService.GetPageSizes(FilePath), _pdfService.GetBookmarks(FilePath)));
            StartupTrace.Mark($"DocumentTabViewModel.LoadAsync.pageSizes ({sizes.Length} pages)");
            if (_disposed) return;

            _renderZoom = _zoom;
            _appliedScaling = _renderScaling;
            var pages = new ObservableCollection<PdfPageViewModel>(
                sizes.Select((size, index) =>
                    new PdfPageViewModel(index, size.Width, size.Height, _zoom)));
            PageCount = sizes.Length;
            Pages = pages;
            SetTableOfContents(bookmarks);
            StartupTrace.Mark("DocumentTabViewModel.LoadAsync.pagesCreated");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"无法显示此 PDF：{ex.Message}";
        }
        finally
        {
            _loadInProgress = false;
            IsLoading = false;
            StartupTrace.Mark("DocumentTabViewModel.LoadAsync.exit");
        }
    }

    /// <summary>Requests a render without discarding the bitmap already on screen.</summary>
    public void EnsurePageRendered(PdfPageViewModel page, bool isVisible = true)
    {
        if (_disposed) return;
        _realizedPages.Add(page);
        if (isVisible) _visiblePages.Add(page);
        else _visiblePages.Remove(page);
        if (isVisible) _ = ObserveVisiblePageContentAsync(page);
        else
        {
            CancelAutomaticTextLoad(page);
            CancelAutomaticLinkLoadsExcept(_visiblePages);
        }
        if (isVisible) _ = ObserveHighlightLoadAsync(page);
        page.SetZoom(_zoom, notify: true);

        if (page.Image is not null && IsRenderedAtCurrentScale(page))
        {
            TouchCache(page);
            return;
        }

        if (_renderingSuspended) return;
        EnqueueRender(page, isVisible);
    }

    public void ReleasePage(PdfPageViewModel page)
    {
        _realizedPages.Remove(page);
        _visiblePages.Remove(page);
        CancelAutomaticLinkLoadsExcept(_visiblePages);
        CancelAutomaticTextLoad(page);
        if (_renderRequests.TryGetValue(page.PageIndex, out RenderRequest? request))
        {
            if (request.State == RenderRequestState.Queued) RemoveRenderRequest(request);
            else request.Cancel();
        }

        TrimCache();
    }

    /// <summary>
    /// Replaces the realized-page set with the containers the virtualizing
    /// panel actually keeps alive. ContainerClearing cannot be used for this:
    /// Avalonia clears the container's DataContext before raising it.
    /// </summary>
    public void SyncRealizedPages(
        IReadOnlyCollection<PdfPageViewModel> realizedPages,
        IReadOnlyCollection<PdfPageViewModel>? visiblePages = null,
        int scrollDirection = 0,
        int? currentPageIndex = null)
    {
        if (_disposed) return;

        _realizedPages.Clear();
        foreach (PdfPageViewModel page in realizedPages) _realizedPages.Add(page);
        _visiblePages.Clear();
        foreach (PdfPageViewModel page in visiblePages ?? realizedPages) _visiblePages.Add(page);
        CancelAutomaticTextLoadsExcept(_visiblePages);
        CancelAutomaticLinkLoadsExcept(_visiblePages);
        foreach (PdfPageViewModel page in _visiblePages)
        {
            _ = ObserveVisiblePageContentAsync(page);
            _ = ObserveHighlightLoadAsync(page);
        }
        if (currentPageIndex is >= 0)
            CurrentPage = Math.Clamp(currentPageIndex.Value + 1, 1, PageCount);
        else
            UpdateCurrentPage(_visiblePages);
        if (scrollDirection != 0) _scrollDirection = Math.Sign(scrollDirection);

        foreach (RenderRequest request in _renderRequests.Values.ToArray())
        {
            if (!_realizedPages.Contains(request.Page))
            {
                if (request.State == RenderRequestState.Queued) RemoveRenderRequest(request);
                else request.Cancel();
                continue;
            }

            if (request.State == RenderRequestState.Queued)
                MoveRequestToQueue(request, _visiblePages.Contains(request.Page));
        }

        TrimCache();
    }

    /// <summary>
    /// Defers bitmap work while a zoom animation is running: rendering at
    /// intermediate zoom factors would be wasted and would fight the animation
    /// for UI-thread time.
    /// </summary>
    public void SuspendRendering()
    {
        if (_disposed) return;
        _renderingSuspended = true;
    }

    /// <summary>
    /// Ends a rendering suspension and re-renders the visible pages at the
    /// zoom factor the document settled on.
    /// </summary>
    public void ResumeRendering()
    {
        if (_disposed) return;
        _renderingSuspended = false;
        CommitRenderState();
    }

    [RelayCommand]
    private Task RetryAsync() => LoadAsync();

    internal void SetVisualZoom(double? zoom)
    {
        if (Nullable.Equals(_visualZoom, zoom)) return;

        bool wasAnimating = IsZoomAnimating;
        _visualZoom = zoom;
        if (wasAnimating != IsZoomAnimating) OnPropertyChanged(nameof(IsZoomAnimating));
        OnPropertyChanged(nameof(VisualZoom));
        OnPropertyChanged(nameof(ZoomPercent));
    }

    /// <summary>
    /// Applies the current <see cref="Zoom"/> to realized page containers. The
    /// virtualizing panel derives all other page geometry from its base layout.
    /// </summary>
    private void ApplyLayoutZoom()
    {
        foreach (PdfPageViewModel page in _realizedPages) page.SetZoom(_zoom, notify: true);
    }

    /// <summary>
    /// Device pixels per PDF point the page has to be rendered at for a
    /// pixel-exact result on the current screen: layout scale × zoom × screen
    /// scaling, bounded so a single bitmap cannot grow without limit.
    /// </summary>
    private double ComputeRenderScale(PdfPageViewModel page)
    {
        double scale = Math.Min(MaxRenderScale, LayoutScale * _renderZoom * _renderScaling);

        double longestEdge = Math.Max(page.PointWidth, page.PointHeight);
        if (longestEdge * scale > MaxBitmapEdgePixels)
        {
            scale = MaxBitmapEdgePixels / longestEdge;
        }

        return scale;
    }

    /// <summary>
    /// True when the page already has a bitmap that is at least as sharp as the
    /// current zoom/screen scaling requires. Sharper bitmaps are downscaled by
    /// the compositor instead of being rendered again at a lower resolution.
    /// </summary>
    private bool IsRenderedAtCurrentScale(PdfPageViewModel page)
    {
        if (page.Image is null || double.IsNaN(page.RenderedScale)) return false;

        double required = ComputeRenderScale(page);
        PixelSize rendered = PdfPageGeometry.GetPixelSize(page.PointWidth, page.PointHeight, page.RenderedScale);
        PixelSize target = PdfPageGeometry.GetPixelSize(page.PointWidth, page.PointHeight, required);
        return page.RenderedScale >= required - RenderScaleTolerance &&
               rendered.Width >= target.Width && rendered.Height >= target.Height;
    }

    // A small scale change can still cross an integer pixel boundary. Commit
    // the exact state; IsRenderedAtCurrentScale decides whether pixels can be reused.
    private bool RenderStateIsCurrent =>
        _renderZoom.Equals(_zoom) && _appliedScaling.Equals(_renderScaling);

    /// <summary>
    /// Re-renders the visible pages when the zoom factor or the screen scaling
    /// the bitmaps were produced for is out of date.
    /// </summary>
    private void CommitRenderState()
    {
        if (_disposed || _renderingSuspended || RenderStateIsCurrent) return;

        _renderZoom = _zoom;
        _appliedScaling = _renderScaling;
        _renderVersion++;
        CancelAllRenders();

        // Bitmaps already on screen stay visible while the re-render runs.
        foreach (PdfPageViewModel page in _realizedPages.ToArray())
            EnqueueRender(page, _visiblePages.Contains(page));
    }

    private void EnqueueRender(PdfPageViewModel page, bool isVisible)
    {
        if (IsRenderedAtCurrentScale(page)) return;

        if (_renderRequests.TryGetValue(page.PageIndex, out RenderRequest? existing))
        {
            if (existing.State == RenderRequestState.Queued)
            {
                existing.Touch();
                MoveRequestToQueue(existing, isVisible);
                return;
            }

            existing.IsVisible = isVisible;
            if (!existing.IsCancellationRequested) return;
        }

        var request = new RenderRequest(
            page, _renderVersion, ComputeRenderScale(page), isVisible);
        _renderRequests[page.PageIndex] = request;
        AddRequestToQueue(request);
        page.IsLoading = true;
        page.ErrorMessage = null;
        TrimRenderQueue();
        _renderSignal.Release();
    }

    private void AddRequestToQueue(RenderRequest request)
    {
        var queue = request.IsVisible ? _visibleRenderQueue : _prefetchRenderQueue;
        request.QueueNode = queue.AddFirst(request);
    }

    private void MoveRequestToQueue(RenderRequest request, bool isVisible)
    {
        request.QueueNode?.List?.Remove(request.QueueNode);
        request.IsVisible = isVisible;
        AddRequestToQueue(request);
    }

    private async Task RenderWorkerAsync()
    {
        try
        {
            while (true)
            {
                await _renderSignal.WaitAsync(_workerCancellation.Token);
                if (_disposed) return;
                if (!TryDequeue(out RenderRequest request)) continue;
                await RenderPageAsync(request);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _renderSignal.Dispose();
            _workerCancellation.Dispose();
        }
    }

    private bool TryDequeue(out RenderRequest request)
    {
        LinkedListNode<RenderRequest>? node = _visibleRenderQueue.First ?? SelectPrefetchRequest();
        if (node is null)
        {
            request = null!;
            return false;
        }

        node.List!.Remove(node);
        request = node.Value;
        request.QueueNode = null;
        request.State = RenderRequestState.Rendering;
        return true;
    }

    private LinkedListNode<RenderRequest>? SelectPrefetchRequest()
    {
        if (_visiblePages.Count == 0) return _prefetchRenderQueue.First;

        int firstVisible = _visiblePages.Min(page => page.PageIndex);
        int lastVisible = _visiblePages.Max(page => page.PageIndex);
        LinkedListNode<RenderRequest>? best = null;
        (int Group, int Distance) bestKey = (int.MaxValue, int.MaxValue);

        for (LinkedListNode<RenderRequest>? node = _prefetchRenderQueue.First;
             node is not null;
             node = node.Next)
        {
            int index = node.Value.Page.PageIndex;
            (int Group, int Distance) key;
            if (_scrollDirection >= 0)
            {
                key = index > lastVisible
                    ? (0, index - lastVisible)
                    : index < firstVisible ? (1, firstVisible - index) : (0, 0);
            }
            else
            {
                key = index < firstVisible
                    ? (0, firstVisible - index)
                    : index > lastVisible ? (1, index - lastVisible) : (0, 0);
            }

            if (best is null || key.CompareTo(bestKey) < 0 ||
                (key == bestKey && node.Value.LastRequestedAt > best.Value.LastRequestedAt))
            {
                best = node;
                bestKey = key;
            }
        }

        return best;
    }

    private async Task RenderPageAsync(RenderRequest request)
    {
        PdfPageViewModel page = request.Page;
        try
        {
            var bitmap = await Task.Run(
                () => _pdfService.RenderPageToWriteableBitmap(
                    FilePath,
                    page.PageIndex,
                    (float)request.RenderScale,
                    request.CancellationToken,
                    request.PixelSize),
                request.CancellationToken);

            if (!CanAcceptRender(request))
            {
                bitmap.Dispose();
                return;
            }

            page.SetImage(bitmap, request.RenderScale);
            page.ErrorMessage = null;
            TouchCache(page);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (CanAcceptRender(request)) page.ErrorMessage = ex.Message;
        }
        finally
        {
            CompleteRenderRequest(request);
            request.Dispose();
        }
    }

    private bool CanAcceptRender(RenderRequest request) =>
        !_disposed &&
        !request.IsCancellationRequested &&
        request.RenderVersion == _renderVersion &&
        _realizedPages.Contains(request.Page) &&
        _renderRequests.TryGetValue(request.Page.PageIndex, out RenderRequest? current) &&
        ReferenceEquals(current, request);

    private void CompleteRenderRequest(RenderRequest request)
    {
        if (_renderRequests.TryGetValue(request.Page.PageIndex, out RenderRequest? current) &&
            ReferenceEquals(current, request))
        {
            _renderRequests.Remove(request.Page.PageIndex);
        }

        if (!_renderRequests.ContainsKey(request.Page.PageIndex))
            request.Page.IsLoading = false;
    }

    private void TrimRenderQueue()
    {
        int limit = Math.Max(MaxQueuedRenderRequests, _realizedPages.Count);
        while (QueuedRenderCount > limit)
        {
            RenderRequest? request = (_prefetchRenderQueue.Last ?? _visibleRenderQueue.Last)?.Value;
            if (request is null) break;
            RemoveRenderRequest(request);
        }
    }

    private void RemoveRenderRequest(RenderRequest request)
    {
        request.QueueNode?.List?.Remove(request.QueueNode);
        request.QueueNode = null;
        if (_renderRequests.TryGetValue(request.Page.PageIndex, out RenderRequest? current) &&
            ReferenceEquals(current, request))
        {
            _renderRequests.Remove(request.Page.PageIndex);
        }

        request.Page.IsLoading = false;
        request.Cancel();
        request.Dispose();
    }

    private void TouchCache(PdfPageViewModel page)
    {
        if (page.Image is null) return;

        // Remove first: it subtracts the size the entry was accounted with.
        // Reading it back after a re-render would subtract the new bitmap's
        // size and leave the entry counted at its original, smaller size.
        RemoveCacheEntry(page);

        _cacheNodes[page.PageIndex] = _cacheLru.AddFirst(page);
        long bytes = GetImageBytes(page);
        _cacheEntryBytes[page.PageIndex] = bytes;
        _cacheBytes += bytes;
        TrimCache();
    }

    private void TrimCache()
    {
        while (_cacheBytes > MaxBitmapCacheBytes)
        {
            LinkedListNode<PdfPageViewModel>? node = _cacheLru.Last;
            while (node is not null && _realizedPages.Contains(node.Value)) node = node.Previous;
            if (node is null) break;

            PdfPageViewModel page = node.Value;
            RemoveCacheEntry(page);
            page.ClearImage();
        }
    }

    private void RemoveCacheEntry(PdfPageViewModel page)
    {
        if (!_cacheNodes.Remove(page.PageIndex, out LinkedListNode<PdfPageViewModel>? node)) return;
        _cacheLru.Remove(node);

        if (_cacheEntryBytes.Remove(page.PageIndex, out long bytes))
            _cacheBytes -= bytes;
        else if (page.Image is not null)
            _cacheBytes -= GetImageBytes(page);
    }

    private static long GetImageBytes(PdfPageViewModel page)
    {
        var size = page.Image!.PixelSize;
        return (long)size.Width * size.Height * 4;
    }

    private void CancelAllRenders()
    {
        foreach (RenderRequest request in _renderRequests.Values)
        {
            request.Page.IsLoading = false;
            request.Cancel();
            if (request.State == RenderRequestState.Queued) request.Dispose();
        }

        _visibleRenderQueue.Clear();
        _prefetchRenderQueue.Clear();
        _renderRequests.Clear();
    }

    private void ClearBitmaps()
    {
        foreach (var page in Pages) page.ClearImage();
        _cacheLru.Clear();
        _cacheNodes.Clear();
        _cacheEntryBytes.Clear();
        _cacheBytes = 0;
    }

    private void StopRendering()
    {
        _renderVersion++;
        _renderingSuspended = false;
        CancelAllRenders();
        _realizedPages.Clear();
        _visiblePages.Clear();
        CancelAutomaticTextLoadsExcept(_visiblePages);
        CancelAutomaticLinkLoadsExcept(_visiblePages);
        _renderZoom = _zoom;
        _appliedScaling = _renderScaling;
    }

    private void ClearPages()
    {
        StopRendering();
        ClearBitmaps();
        Pages = [];
        PageCount = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _searchCancellation?.Cancel();
        ClearPages();
        _workerCancellation.Cancel();
        _renderSignal.Release();
        _pdfService.ReleaseDocument(FilePath);
    }

    private enum RenderRequestState
    {
        Queued,
        Rendering,
    }

    private sealed class RenderRequest(
        PdfPageViewModel page,
        int renderVersion,
        double renderScale,
        bool isVisible) : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();

        public PdfPageViewModel Page { get; } = page;
        public int RenderVersion { get; } = renderVersion;
        public double RenderScale { get; } = renderScale;
        public PixelSize PixelSize { get; } =
            PdfPageGeometry.GetPixelSize(page.PointWidth, page.PointHeight, renderScale);
        public bool IsVisible { get; set; } = isVisible;
        public RenderRequestState State { get; set; } = RenderRequestState.Queued;
        public DateTimeOffset LastRequestedAt { get; private set; } = DateTimeOffset.UtcNow;
        public LinkedListNode<RenderRequest>? QueueNode { get; set; }
        public CancellationToken CancellationToken => _cancellation.Token;
        public bool IsCancellationRequested => _cancellation.IsCancellationRequested;

        public void Touch() => LastRequestedAt = DateTimeOffset.UtcNow;
        public void Cancel() => _cancellation.Cancel();
        public void Dispose() => _cancellation.Dispose();
    }
}
