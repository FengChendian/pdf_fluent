using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using pdf_fluent.ViewModels;
using pdf_fluent.Views;
using Xunit;
using static pdf_fluent.Tests.PdfTestHelpers;

namespace pdf_fluent.Tests;

public sealed class PdfVirtualizationMemoryTests
{
    [AvaloniaFact]
    public async Task ScrollingThroughPagesEvictsOffscreenBitmaps()
    {
        const int pageCount = 60;
        const int renderWidth = 1200;
        const int renderHeight = 1600;
        const long cacheLimit = 256L * 1024 * 1024;

        using var service = new FakePdfDocumentService(pageCount, renderWidth, renderHeight);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        service.AllowRenders();

        var window = new Window
        {
            Width = 1200,
            Height = 900,
            Content = new PdfDocumentView { DataContext = document },
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            var scrollViewer = window.GetVisualDescendants().OfType<ScrollViewer>().First();
            var itemsControl = window.GetVisualDescendants().OfType<ItemsControl>().First();
            double maxOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);

            for (double target = 0; ; target += 700)
            {
                scrollViewer.Offset = new Vector(0, Math.Min(target, maxOffset));
                window.UpdateLayout();
                await WaitUntilAsync(
                    () => AllRealizedPagesResolved(itemsControl), TimeSpan.FromSeconds(10));

                if (scrollViewer.Offset.Y >= maxOffset - 0.5) break;
            }

            int livePages = document.Pages.Count(page => page.Image is not null);
            long liveBytes = LiveBitmapBytes(document);

            Assert.True(
                livePages < pageCount,
                $"滚动完全部 {pageCount} 页后仍有 {livePages} 页位图未被释放。");
            Assert.True(
                liveBytes <= cacheLimit,
                $"离屏位图未被淘汰：{livePages} 页，{liveBytes / 1024.0 / 1024.0:F1} MiB。");
            Assert.Null(document.Pages[0].Image);
            Assert.NotNull(document.Pages[^1].Image);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ZoomingInAfterScrollingKeepsTheBitmapCacheWithinBudget()
    {
        const int pageCount = 60;
        const int renderWidth = 1200;
        const int renderHeight = 1600;
        const long cacheLimit = 256L * 1024 * 1024;

        // Bitmaps grow with the render scale, like real PDFium output. Zooming
        // re-renders the realized pages at a larger size while their page
        // indexes stay the same, so the cache has to update its byte
        // accounting when a bitmap is replaced - otherwise the budget stops
        // triggering evictions and the live bitmaps keep growing.
        using var service = new FakePdfDocumentService(
            pageCount, renderWidth, renderHeight, scaleRenderSize: true);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        service.AllowRenders();

        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1200, Height = 900, Content = view };

        try
        {
            window.Show();
            window.UpdateLayout();

            var scrollViewer = window.GetVisualDescendants().OfType<ScrollViewer>().First();
            var itemsControl = window.GetVisualDescendants().OfType<ItemsControl>().First();

            for (double target = 0; ; target += 1400)
            {
                scrollViewer.Offset = new Vector(0, target);
                window.UpdateLayout();
                await WaitUntilAsync(
                    () => AllRealizedPagesResolved(itemsControl), TimeSpan.FromSeconds(10));
                if (scrollViewer.Offset.Y >= Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height) - 0.5)
                    break;
            }

            Assert.True(LiveBitmapBytes(document) <= cacheLimit, "滚动后位图缓存已超出预算。");

            view.EnsureZoomController();
            view.ZoomController!.ZoomTo(1.5, anchorViewportPoint: null, animate: false);
            window.UpdateLayout();

            await WaitUntilAsync(
                () => AllRealizedPagesRendered(itemsControl, document), TimeSpan.FromSeconds(10));
            // Let the panel publish its final realized set so the cache trims
            // against the pages that are actually kept alive.
            window.UpdateLayout();
            HashSet<PdfPageViewModel> realized = itemsControl.GetRealizedContainers()
                .Select(container => container.DataContext as PdfPageViewModel)
                .OfType<PdfPageViewModel>()
                .ToHashSet();
            string dump = string.Join(", ", document.Pages
                .Where(page => page.Image is not null)
                .Select(page =>
                    $"#{page.PageIndex}:{page.Image!.PixelSize.Width}x{page.Image.PixelSize.Height}" +
                    $"{(realized.Contains(page) ? "(realized)" : "(cached)")}"));
            Assert.True(
                LiveBitmapBytes(document) <= cacheLimit,
                $"放大重新渲染后位图缓存超出预算：{LiveBitmapBytes(document) / 1024.0 / 1024.0:F1} MiB " +
                $"(realized={realized.Count}/{document.RealizedPageCount}, " +
                $"cachedPages={document.CachedPageCount}): {dump}");
            Assert.Equal(LiveBitmapBytes(document), document.CachedBitmapBytes);
        }
        finally
        {
            window.Close();
        }
    }

    private static bool AllRealizedPagesRendered(ItemsControl itemsControl, DocumentTabViewModel document)
    {
        double required = Math.Min(
            DocumentTabViewModel.MaxRenderScale,
            DocumentTabViewModel.LayoutScale * document.Zoom * document.RenderScaling);
        return AllRealizedPagesResolved(itemsControl) &&
               itemsControl.GetRealizedContainers()
                   .Select(container => container.DataContext as PdfPageViewModel)
                   .All(page => page is null || page.RenderedScale >= required - 0.005);
    }

    private static bool AllRealizedPagesResolved(ItemsControl itemsControl) =>
        itemsControl.GetRealizedContainers()
            .Select(container => container.DataContext as PdfPageViewModel)
            .All(page => page is null || page.Image is not null || page.ErrorMessage is not null);
}
