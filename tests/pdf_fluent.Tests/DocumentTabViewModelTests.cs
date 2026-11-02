using Avalonia.Headless.XUnit;
using pdf_fluent.ViewModels;
using Xunit;
using static pdf_fluent.Tests.PdfTestHelpers;

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
    public async Task ReleasedPageRetainsBitmapBelowCacheLimit()
    {
        using var service = new FakePdfDocumentService(1);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        service.AllowRenders();

        PdfPageViewModel page = document.Pages[0];
        document.EnsurePageRendered(page);
        await WaitUntilAsync(() => page.Image is not null);
        var bitmap = page.Image;

        document.ReleasePage(page);

        Assert.Same(bitmap, page.Image);
    }

    [AvaloniaFact]
    public async Task CacheRetainsExactly256MiBAndEvictsLruBitmapAboveLimit()
    {
        const int renderWidth = 2048;
        const int renderHeight = 1024;
        const int bytesPerPage = renderWidth * renderHeight * 4;
        const int pagesAtCacheLimit = 256 * 1024 * 1024 / bytesPerPage;
        using var service = new FakePdfDocumentService(
            pagesAtCacheLimit + 1,
            renderWidth,
            renderHeight);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        service.AllowRenders();

        for (int index = 0; index < pagesAtCacheLimit; index++)
        {
            PdfPageViewModel page = document.Pages[index];
            document.EnsurePageRendered(page);
            await WaitUntilAsync(() => page.Image is not null);
            document.ReleasePage(page);
        }

        Assert.All(document.Pages.Take(pagesAtCacheLimit), page => Assert.NotNull(page.Image));
        var evictedBitmap = document.Pages[0].Image;

        PdfPageViewModel extraPage = document.Pages[pagesAtCacheLimit];
        document.EnsurePageRendered(extraPage);
        await WaitUntilAsync(() => extraPage.Image is not null);
        document.ReleasePage(extraPage);

        Assert.Null(document.Pages[0].Image);
        Assert.Throws<ObjectDisposedException>(() =>
        {
            evictedBitmap!.Lock();
        });
        Assert.All(document.Pages.Skip(1), page => Assert.NotNull(page.Image));
    }

    [AvaloniaFact]
    public async Task SyncRealizedPagesDropsQueuedWorkForRecycledPages()
    {
        using var service = new FakePdfDocumentService(3);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();

        try
        {
            document.EnsurePageRendered(document.Pages[0]);
            await service.RenderStarted.WaitAsync(TimeSpan.FromSeconds(2));
            document.EnsurePageRendered(document.Pages[1]);
            document.EnsurePageRendered(document.Pages[2]);
            Assert.Equal(3, document.PendingRenderCount);
            Assert.Equal(2, document.QueuedRenderCount);

            document.SyncRealizedPages(new[] { document.Pages[2] });

            Assert.Equal(2, document.PendingRenderCount);
            Assert.Equal(1, document.QueuedRenderCount);

            service.AllowRenders();
            await WaitUntilAsync(() => document.Pages[2].Image is not null);

            Assert.Equal(new[] { 0, 2 }, service.RenderedPages);
            Assert.Null(document.Pages[1].Image);
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

    [AvaloniaFact]
    public async Task VisiblePageRunsBeforePrefetchPages()
    {
        using var service = new FakePdfDocumentService(5);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();

        try
        {
            document.EnsurePageRendered(document.Pages[0]);
            await service.RenderStarted.WaitAsync(TimeSpan.FromSeconds(2));
            document.EnsurePageRendered(document.Pages[1], isVisible: false);
            document.EnsurePageRendered(document.Pages[2], isVisible: false);
            document.EnsurePageRendered(document.Pages[3], isVisible: true);

            service.AllowRenders();
            await WaitUntilAsync(() => service.RenderedPages.Length >= 2);

            Assert.Equal(new[] { 0, 3 }, service.RenderedPages.Take(2).ToArray());
        }
        finally
        {
            service.AllowRenders();
        }
    }

    [AvaloniaFact]
    public async Task PrefetchRequestsPreferPagesInScrollDirection()
    {
        using var service = new FakePdfDocumentService(20);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();

        try
        {
            document.EnsurePageRendered(document.Pages[10]);
            await service.RenderStarted.WaitAsync(TimeSpan.FromSeconds(2));
            foreach (int index in new[] { 8, 9, 11, 12 })
                document.EnsurePageRendered(document.Pages[index], isVisible: false);

            document.SyncRealizedPages(
                new[] { 8, 9, 10, 11, 12 }.Select(index => document.Pages[index]).ToArray(),
                new[] { document.Pages[10] },
                scrollDirection: -1);
            service.AllowRenders();
            await WaitUntilAsync(() => service.RenderedPages.Length >= 2);

            Assert.Equal(9, service.RenderedPages[1]);
        }
        finally
        {
            service.AllowRenders();
        }
    }

    [AvaloniaFact]
    public async Task CancelledRenderingCanBeRequestedAgainImmediately()
    {
        using var service = new FakePdfDocumentService(1);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();

        try
        {
            PdfPageViewModel page = document.Pages[0];
            document.EnsurePageRendered(page);
            await service.RenderStarted.WaitAsync(TimeSpan.FromSeconds(2));

            document.ReleasePage(page);
            document.EnsurePageRendered(page);
            service.AllowRenders();
            await WaitUntilAsync(() => page.Image is not null);

            Assert.Equal(new[] { 0, 0 }, service.RenderedPages);
            Assert.Equal(0, document.PendingRenderCount);
        }
        finally
        {
            service.AllowRenders();
        }
    }

    [AvaloniaFact]
    public async Task CancellingReRenderingKeepsTheCachedBitmapAndAccounting()
    {
        using var service = new FakePdfDocumentService(1, 320, 400, scaleRenderSize: true);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        service.AllowRenders();

        try
        {
            PdfPageViewModel page = document.Pages[0];
            document.EnsurePageRendered(page);
            await WaitUntilAsync(() => page.Image is not null);
            var cachedBitmap = page.Image;
            long cachedBytes = document.CachedBitmapBytes;

            service.BlockRenders();
            document.RenderScaling = 2.0;
            await WaitUntilAsync(() => service.RenderCallCount >= 2);
            document.ReleasePage(page);
            await WaitUntilAsync(() => document.PendingRenderCount == 0);

            Assert.Same(cachedBitmap, page.Image);
            Assert.Equal(cachedBytes, document.CachedBitmapBytes);
            Assert.Equal(1, document.CachedPageCount);
        }
        finally
        {
            service.AllowRenders();
        }
    }

    [AvaloniaFact]
    public async Task CacheBudgetUsesActualBitmapSizeAfterReRenderingAtHigherScale()
    {
        // 320x400 pt pages render to 400x500 px at the 1.25 px/pt baseline
        // (0.76 MiB) and to 1600x2000 px at the 5.0 px/pt cap (12.2 MiB) once
        // the window is on a 400% screen. Re-rendering a cached page replaces
        // its bitmap without changing the page index, so the LRU has to
        // account the new size instead of the one it first stored. 24 large
        // bitmaps are 293 MiB, i.e. above the 256 MiB budget.
        const int pageCount = 32;
        const int pagesRendered = 24;
        const long cacheLimit = 256L * 1024 * 1024;

        using var service = new FakePdfDocumentService(
            pageCount, renderWidth: 320, renderHeight: 400, scaleRenderSize: true);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        service.AllowRenders();

        for (int index = 0; index < pagesRendered; index++)
        {
            PdfPageViewModel page = document.Pages[index];
            document.EnsurePageRendered(page);
            await WaitUntilAsync(() => page.Image is not null);
            document.ReleasePage(page);
        }

        Assert.Equal(pagesRendered, document.CachedPageCount);

        document.RenderScaling = 4.0;
        for (int index = 0; index < pagesRendered; index++)
        {
            PdfPageViewModel page = document.Pages[index];
            document.EnsurePageRendered(page);
            await WaitUntilAsync(
                () => page.Image is not null && Math.Abs(page.RenderedScale - 5.0) < 0.01);
            document.ReleasePage(page);
        }

        long liveBytes = document.Pages
            .Where(page => page.Image is not null)
            .Sum(page => (long)page.Image!.PixelSize.Width * page.Image.PixelSize.Height * 4);

        Assert.True(
            liveBytes <= cacheLimit,
            $"位图缓存超出预算：实际 {liveBytes / 1024.0 / 1024.0:F1} MiB，" +
            $"上限 {cacheLimit / 1024.0 / 1024.0:F1} MiB。");
        Assert.True(
            document.CachedPageCount < pagesRendered,
            $"重新渲染放大位图后没有淘汰任何缓存页：{document.CachedPageCount} 页仍被保留。");
        Assert.Equal(liveBytes, document.CachedBitmapBytes);
    }

    [AvaloniaFact]
    public async Task SwitchingAwayReleasesAllCachedBitmaps()
    {
        using var service = new FakePdfDocumentService(3, 640, 800);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        service.AllowRenders();

        var bitmaps = new List<Avalonia.Media.Imaging.WriteableBitmap>();
        for (int index = 0; index < document.Pages.Count; index++)
        {
            PdfPageViewModel page = document.Pages[index];
            document.EnsurePageRendered(page);
            await WaitUntilAsync(() => page.Image is not null);
            document.ReleasePage(page);
            bitmaps.Add(page.Image!);
        }

        Assert.Equal(3, document.CachedPageCount);
        Assert.True(document.CachedBitmapBytes > 0);

        document.IsSelected = true;
        document.IsSelected = false;

        Assert.All(document.Pages, page => Assert.Null(page.Image));
        Assert.Equal(0, document.CachedPageCount);
        Assert.Equal(0, document.CachedBitmapBytes);
        Assert.All(bitmaps, bitmap =>
            Assert.Throws<ObjectDisposedException>(() => bitmap.Lock()));
    }

}
