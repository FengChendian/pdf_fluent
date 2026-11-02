using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using pdf_fluent.Services;

namespace pdf_fluent.ViewModels;

public sealed partial class DocumentTabViewModel : ViewModelBase, IDisposable
{
    private const double BaseRenderScale = 1.25;
    private const long MaxBitmapCacheBytes = 128 * 1024 * 1024;
    private const int MaxQueuedRenderRequests = 32;
    private readonly IPdfDocumentService _pdfService;
    private readonly HashSet<PdfPageViewModel> _realizedPages = new();
    private readonly LinkedList<RenderRequest> _renderQueue = new();
    private readonly Dictionary<int, RenderRequest> _renderRequests = new();
    private readonly LinkedList<PdfPageViewModel> _cacheLru = new();
    private readonly Dictionary<int, LinkedListNode<PdfPageViewModel>> _cacheNodes = new();
    private readonly SemaphoreSlim _renderSignal = new(0);
    private readonly CancellationTokenSource _workerCancellation = new();
    private bool _loadInProgress;
    private bool _disposed;
    private int _renderVersion;
    private long _cacheBytes;

    public DocumentTabViewModel(string filePath, IPdfDocumentService pdfService)
    {
        FilePath = filePath;
        _pdfService = pdfService;
        _ = RenderWorkerAsync();
    }

    public string FilePath { get; }
    public string FileName => Path.GetFileNameWithoutExtension(FilePath);
    public int ZoomPercent => (int)Math.Round(Zoom * 100);
    public bool IsLoaded => !IsLoading;
    internal int PendingRenderCount => _renderRequests.Count;
    internal int QueuedRenderCount => _renderQueue.Count;

    [ObservableProperty]
    private ObservableCollection<PdfPageViewModel> _pages = [];

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private int _pageCount;

    [ObservableProperty]
    private double _zoom = 1;

    [ObservableProperty]
    private int _layoutVersion;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnZoomChanged(double value)
    {
        OnPropertyChanged(nameof(ZoomPercent));
        if (Pages.Count == 0) return;

        _renderVersion++;
        CancelAllRenders();
        ClearBitmaps();
        float renderScale = (float)(BaseRenderScale * value);
        foreach (var page in Pages) page.SetRenderScale(renderScale);
        LayoutVersion++;
        foreach (var page in _realizedPages) EnsurePageRendered(page);
    }

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsLoaded));

    partial void OnIsSelectedChanged(bool value)
    {
        if (!value) ReleaseRenderingResources();
    }

    public async Task LoadAsync()
    {
        if (_loadInProgress || _disposed) return;

        _loadInProgress = true;
        IsLoading = true;
        ErrorMessage = null;
        ClearPages();

        try
        {
            var sizes = await Task.Run(() => _pdfService.GetPageSizes(FilePath));
            if (_disposed) return;

            float renderScale = (float)(BaseRenderScale * Zoom);
            var pages = new ObservableCollection<PdfPageViewModel>(
                sizes.Select((size, index) =>
                    new PdfPageViewModel(index, size.Width, size.Height, renderScale)));
            PageCount = sizes.Length;
            Pages = pages;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"无法显示此 PDF：{ex.Message}";
        }
        finally
        {
            _loadInProgress = false;
            IsLoading = false;
        }
    }

    public void EnsurePageRendered(PdfPageViewModel page)
    {
        if (_disposed) return;
        _realizedPages.Add(page);
        if (page.Image is not null)
        {
            TouchCache(page);
            return;
        }

        if (_renderRequests.TryGetValue(page.PageIndex, out RenderRequest? existing))
        {
            existing.Touch();
            if (existing.QueueNode is { } node)
            {
                _renderQueue.Remove(node);
                _renderQueue.AddFirst(node);
            }

            return;
        }

        var request = new RenderRequest(
            page,
            _renderVersion,
            (float)(BaseRenderScale * Zoom));
        request.QueueNode = _renderQueue.AddFirst(request);
        _renderRequests.Add(page.PageIndex, request);
        page.IsLoading = true;
        page.ErrorMessage = null;
        TrimRenderQueue();
        _renderSignal.Release();
    }

    public void ReleasePage(PdfPageViewModel page)
    {
        _realizedPages.Remove(page);
        if (_renderRequests.TryGetValue(page.PageIndex, out RenderRequest? request) &&
            request.State == RenderRequestState.Queued)
        {
            RemoveRenderRequest(request);
        }

        TrimCache();
    }

    [RelayCommand]
    private Task RetryAsync() => LoadAsync();

    [RelayCommand]
    private void ZoomIn()
    {
        if (!IsLoading && Zoom < 2.5) Zoom += 0.25;
    }

    [RelayCommand]
    private void ZoomOut()
    {
        if (!IsLoading && Zoom > 0.5) Zoom -= 0.25;
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
        request = null!;
        if (_renderQueue.First is not { } node) return false;
        _renderQueue.Remove(node);
        request = node.Value;
        request.QueueNode = null;
        request.State = RenderRequestState.Rendering;
        return true;
    }

    private async Task RenderPageAsync(RenderRequest request)
    {
        PdfPageViewModel page = request.Page;
        try
        {
            var raw = await Task.Run(() => _pdfService.RenderPageToRawBuffer(
                FilePath,
                page.PageIndex,
                request.RenderScale));

            if (!CanAcceptRender(request)) return;
            var bitmap = PDFiumService.CreateWriteableBitmapFromRawBuffer(
                raw.width,
                raw.height,
                raw.pixels);
            if (!CanAcceptRender(request))
            {
                bitmap.Dispose();
                return;
            }

            page.Image = bitmap;
            page.ErrorMessage = null;
            TouchCache(page);
        }
        catch (Exception ex)
        {
            if (CanAcceptRender(request)) page.ErrorMessage = ex.Message;
        }
        finally
        {
            CompleteRenderRequest(request);
        }
    }

    private bool CanAcceptRender(RenderRequest request) =>
        !_disposed &&
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
        {
            request.Page.IsLoading = false;
        }
    }

    private void TrimRenderQueue()
    {
        int limit = Math.Max(MaxQueuedRenderRequests, _realizedPages.Count);
        while (_renderQueue.Count > limit && _renderQueue.Last is { } node)
        {
            RemoveRenderRequest(node.Value);
        }
    }

    private void RemoveRenderRequest(RenderRequest request)
    {
        if (request.QueueNode is { } node) _renderQueue.Remove(node);
        request.QueueNode = null;
        _renderRequests.Remove(request.Page.PageIndex);
        request.Page.IsLoading = false;
    }

    private void TouchCache(PdfPageViewModel page)
    {
        if (page.Image is null) return;
        RemoveCacheEntry(page);
        _cacheNodes[page.PageIndex] = _cacheLru.AddFirst(page);
        _cacheBytes += GetImageBytes(page);
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
        _cacheBytes -= GetImageBytes(page);
    }

    private static long GetImageBytes(PdfPageViewModel page)
    {
        var size = page.Image!.PixelSize;
        return (long)size.Width * size.Height * 4;
    }

    private void CancelAllRenders()
    {
        foreach (var request in _renderRequests.Values) request.Page.IsLoading = false;
        _renderQueue.Clear();
        _renderRequests.Clear();
    }

    private void ClearBitmaps()
    {
        foreach (var page in Pages) page.ClearImage();
        _cacheLru.Clear();
        _cacheNodes.Clear();
        _cacheBytes = 0;
    }

    private void ReleaseRenderingResources()
    {
        _renderVersion++;
        CancelAllRenders();
        _realizedPages.Clear();
        ClearBitmaps();
    }

    private void ClearPages()
    {
        ReleaseRenderingResources();
        Pages = [];
        PageCount = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearPages();
        _workerCancellation.Cancel();
        _renderSignal.Release();
    }

    private enum RenderRequestState
    {
        Queued,
        Rendering,
    }

    private sealed class RenderRequest(
        PdfPageViewModel page,
        int renderVersion,
        float renderScale)
    {
        public PdfPageViewModel Page { get; } = page;
        public int RenderVersion { get; } = renderVersion;
        public float RenderScale { get; } = renderScale;
        public RenderRequestState State { get; set; } = RenderRequestState.Queued;
        public DateTimeOffset LastRequestedAt { get; private set; } = DateTimeOffset.UtcNow;
        public LinkedListNode<RenderRequest>? QueueNode { get; set; }

        public void Touch() => LastRequestedAt = DateTimeOffset.UtcNow;
    }
}
