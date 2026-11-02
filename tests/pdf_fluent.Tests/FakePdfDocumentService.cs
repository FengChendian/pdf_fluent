using pdf_fluent.Services;

namespace pdf_fluent.Tests;

internal sealed class FakePdfDocumentService : IPdfDocumentService, IDisposable
{
    private readonly ManualResetEventSlim _renderGate = new(false);
    private readonly TaskCompletionSource _renderStarted = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<int> _renderedPages = new();
    private readonly List<float> _renderScales = new();
    private readonly bool _scaleRenderSize;
    private readonly int _pageCount;
    private readonly int _renderWidth;
    private readonly int _renderHeight;

    public FakePdfDocumentService(
        int pageCount,
        int renderWidth = 1,
        int renderHeight = 1,
        bool scaleRenderSize = false)
    {
        _pageCount = pageCount;
        _renderWidth = renderWidth;
        _renderHeight = renderHeight;
        _scaleRenderSize = scaleRenderSize;
    }

    public Task RenderStarted => _renderStarted.Task;
    public int RenderCallCount
    {
        get
        {
            lock (_renderedPages) return _renderedPages.Count;
        }
    }

    public int[] RenderedPages
    {
        get
        {
            lock (_renderedPages) return _renderedPages.ToArray();
        }
    }

    /// <summary>Render scales (device pixels per PDF point) of every render call.</summary>
    public float[] RenderScales
    {
        get
        {
            lock (_renderedPages) return _renderScales.ToArray();
        }
    }

    public (double Width, double Height)[] GetPageSizes(string filePath) =>
        Enumerable.Range(0, _pageCount)
            .Select(index => ((double)600, (double)(800 + index % 3 * 100)))
            .ToArray();

    public Avalonia.Media.Imaging.WriteableBitmap RenderPageToWriteableBitmap(
        string filePath,
        int pageNumber = 0,
        float scale = 1f,
        CancellationToken cancellationToken = default)
    {
        lock (_renderedPages)
        {
            _renderedPages.Add(pageNumber);
            _renderScales.Add(scale);
        }
        _renderStarted.TrySetResult();
        _renderGate.Wait(cancellationToken);

        // Real PDFium bitmaps grow with the render scale; tests that model the
        // bitmap cache budget enable this so a re-render at a higher zoom
        // produces a bigger bitmap instead of the same fixed-size one.
        int width = _scaleRenderSize ? Math.Max(1, (int)Math.Round(_renderWidth * scale)) : _renderWidth;
        int height = _scaleRenderSize ? Math.Max(1, (int)Math.Round(_renderHeight * scale)) : _renderHeight;
        return new Avalonia.Media.Imaging.WriteableBitmap(
            new Avalonia.PixelSize(width, height),
            new Avalonia.Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Premul);
    }

    public void ReleaseDocument(string filePath)
    {
    }

    public void BlockRenders() => _renderGate.Reset();
    public void AllowRenders() => _renderGate.Set();

    public void Dispose()
    {
        _renderGate.Set();
        _renderGate.Dispose();
    }
}
