using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using pdf_fluent.ViewModels;
using pdf_fluent.Views;
using Xunit;

namespace pdf_fluent.Tests;

public sealed class PdfVirtualizationMemoryTests
{
    [AvaloniaFact]
    public async Task ScrollingThroughPagesEvictsOffscreenBitmaps()
    {
        const int pageCount = 60;
        const int renderWidth = 1200;
        const int renderHeight = 1600;
        const long cacheLimit = 128L * 1024 * 1024;

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
                await WaitUntilAsync(() => AllRealizedPagesResolved(itemsControl));

                if (scrollViewer.Offset.Y >= maxOffset - 0.5) break;
            }

            int livePages = document.Pages.Count(page => page.Image is not null);
            long liveBytes = document.Pages
                .Where(page => page.Image is not null)
                .Sum(page => (long)page.Image!.PixelSize.Width * page.Image.PixelSize.Height * 4);

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

    private static bool AllRealizedPagesResolved(ItemsControl itemsControl) =>
        itemsControl.GetRealizedContainers()
            .Select(container => container.DataContext as PdfPageViewModel)
            .All(page => page is null || page.Image is not null || page.ErrorMessage is not null);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("等待分页渲染/布局完成超时。");
            await Task.Delay(10);
        }
    }
}
