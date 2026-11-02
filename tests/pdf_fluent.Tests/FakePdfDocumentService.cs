using pdf_fluent.Services;

namespace pdf_fluent.Tests;

internal sealed class FakePdfDocumentService : IPdfDocumentService, IDisposable
{
    private readonly ManualResetEventSlim _renderGate = new(false);
    private readonly TaskCompletionSource _renderStarted = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<int> _renderedPages = new();
    private readonly int _pageCount;

    public FakePdfDocumentService(int pageCount) => _pageCount = pageCount;

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

    public (double Width, double Height)[] GetPageSizes(string filePath) =>
        Enumerable.Range(0, _pageCount)
            .Select(index => ((double)600, (double)(800 + index % 3 * 100)))
            .ToArray();

    public (int width, int height, byte[] pixels) RenderPageToRawBuffer(
        string filePath,
        int pageNumber = 0,
        float scale = 1f)
    {
        lock (_renderedPages) _renderedPages.Add(pageNumber);
        _renderStarted.TrySetResult();
        _renderGate.Wait();
        return (1, 1, [255, 255, 255, 255]);
    }

    public void AllowRenders() => _renderGate.Set();

    public void Dispose()
    {
        _renderGate.Set();
        _renderGate.Dispose();
    }
}
