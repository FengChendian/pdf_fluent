using Avalonia.Headless.XUnit;
using pdf_fluent.ViewModels;
using Xunit;

namespace pdf_fluent.Tests;

public sealed class DocumentTabViewModelTests
{
    [AvaloniaFact]
    public async Task DuplicatePendingRequestRefreshesLruWithoutAddingWork()
    {
        using var service = new FakePdfDocumentService(10);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();

        try
        {
            document.EnsurePageRendered(document.Pages[0]);
            await service.RenderStarted.WaitAsync(TimeSpan.FromSeconds(2));
            document.EnsurePageRendered(document.Pages[1]);
            document.EnsurePageRendered(document.Pages[2]);
            document.EnsurePageRendered(document.Pages[1]);

            Assert.Equal(3, document.PendingRenderCount);
            Assert.Equal(2, document.QueuedRenderCount);

            service.AllowRenders();
            await WaitUntilAsync(() => document.Pages[2].Image is not null);

            Assert.Equal(new[] { 0, 1, 2 }, service.RenderedPages);
            Assert.Equal(0, document.PendingRenderCount);
        }
        finally
        {
            service.AllowRenders();
        }
    }

    [AvaloniaFact]
    public async Task FastScrollingRemovesOffscreenQueuedRequests()
    {
        using var service = new FakePdfDocumentService(100);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();

        try
        {
            document.EnsurePageRendered(document.Pages[0]);
            await service.RenderStarted.WaitAsync(TimeSpan.FromSeconds(2));
            for (int index = 1; index < document.Pages.Count; index++)
            {
                document.EnsurePageRendered(document.Pages[index]);
                document.EnsurePageRendered(document.Pages[index]);
                if (index > 1) document.ReleasePage(document.Pages[index - 1]);
            }

            Assert.Equal(2, document.PendingRenderCount);
            Assert.Equal(1, document.QueuedRenderCount);

            document.ReleasePage(document.Pages[0]);
            service.AllowRenders();
            await WaitUntilAsync(() => document.Pages[^1].Image is not null);

            Assert.Equal(new[] { 0, 99 }, service.RenderedPages);
            Assert.Equal(0, document.PendingRenderCount);
            Assert.Equal(0, document.QueuedRenderCount);
        }
        finally
        {
            service.AllowRenders();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("等待异步渲染完成超时。");
            await Task.Delay(10);
        }
    }
}
