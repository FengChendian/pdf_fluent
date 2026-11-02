using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using pdf_fluent.Services;
using pdf_fluent.ViewModels;
using pdf_fluent.Views;
using Xunit;
using static pdf_fluent.Tests.PdfTestHelpers;

namespace pdf_fluent.Tests;

/// <summary>
/// Page bitmaps have to be rendered at physical pixel resolution: layout units
/// are device independent, so a 200% display needs twice the bitmap resolution
/// a 100% display needs, and the value has to follow the monitor the window is
/// currently on.
/// </summary>
public sealed class PdfDpiTests
{
    [AvaloniaFact]
    public async Task MovingToAHighDpiMonitorRendersBitmapsAtPhysicalPixelResolution()
    {
        string assetPath = SamplePdfPath();
        using var service = new PDFiumService();
        using var document = new DocumentTabViewModel(assetPath, service);
        await document.LoadAsync();
        Assert.Null(document.ErrorMessage);

        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1000, Height = 800, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();

            PdfPageViewModel page = document.Pages[0];
            await WaitUntilAsync(() => page.Image is not null, TimeSpan.FromSeconds(30));

            // On a 100% screen one bitmap pixel covers exactly one layout unit.
            int dipWidth = (int)Math.Round(page.PageWidth);
            Assert.InRange(page.Image!.PixelSize.Width, dipWidth - 2, dipWidth + 2);
            Assert.Equal(1.25, page.RenderedScale, 3);

            // Drag the window to a 200% (4K) monitor: the same page has to be
            // re-rendered with twice the bitmap resolution to stay crisp.
            SimulateMonitorScaling(window, 2.0);
            await WaitUntilAsync(
                () => page.Image is not null && Math.Abs(page.RenderedScale - 2.5) < 0.01,
                TimeSpan.FromSeconds(30));

            Assert.Equal(2.5, page.RenderedScale, 3);
            Assert.InRange(page.Image!.PixelSize.Width, (dipWidth * 2) - 2, (dipWidth * 2) + 2);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task DoublingTheRenderScalingReusesTheSharperBitmapWhenReturning()
    {
        using var service = new FakePdfDocumentService(pageCount: 3);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        service.AllowRenders();

        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1000, Height = 800, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();

            PdfPageViewModel page = document.Pages[0];
            await WaitUntilAsync(() => page.Image is not null, TimeSpan.FromSeconds(5));
            Assert.Equal(1.25, page.RenderedScale, 3);

            document.RenderScaling = 2.0;
            await WaitUntilAsync(
                () => Math.Abs(page.RenderedScale - 2.5) < 0.01,
                TimeSpan.FromSeconds(5));
            Assert.Equal(2.5, page.RenderedScale, 3);
            var sharper = page.Image;
            int renders = service.RenderCallCount;

            document.RenderScaling = 1.0;
            window.UpdateLayout();

            Assert.Same(sharper, page.Image);
            Assert.Equal(2.5, page.RenderedScale, 3);
            Assert.Equal(renders, service.RenderCallCount);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task RenderScaleIsCappedForExtremeZoomAndScreenScaling()
    {
        using var service = new FakePdfDocumentService(pageCount: 2);
        using var document = new DocumentTabViewModel("unused.pdf", service)
        {
            Zoom = DocumentTabViewModel.MaxZoom,
        };
        await document.LoadAsync();
        service.AllowRenders();

        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1000, Height = 800, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();

            // 1.25 layout scale × 2.5 zoom × 2.0 screen scaling = 6.25 would
            // produce huge bitmaps (and text is unreadable at that size
            // anyway), so the render scale is clamped.
            document.RenderScaling = 2.0;
            PdfPageViewModel page = document.Pages[0];
            await WaitUntilAsync(
                () => Math.Abs(page.RenderedScale - DocumentTabViewModel.MaxRenderScale) < 0.01,
                TimeSpan.FromSeconds(5));

            Assert.Equal(DocumentTabViewModel.MaxRenderScale, page.RenderedScale, 3);
            Assert.All(service.RenderScales, scale =>
                Assert.True(scale <= DocumentTabViewModel.MaxRenderScale + 0.001f));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task MonitorScalingChangeReRendersVisiblePagesAfterDebounce()
    {
        using var service = new FakePdfDocumentService(pageCount: 3);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        service.AllowRenders();

        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1000, Height = 800, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();

            Assert.Equal(window.RenderScaling, document.RenderScaling, 3);

            // Simulate dragging the window from a 100% monitor to a 200% one.
            SimulateMonitorScaling(window, 2.0);

            await WaitUntilAsync(
                () => Math.Abs(document.RenderScaling - 2.0) < 0.01,
                TimeSpan.FromSeconds(5));

            PdfPageViewModel page = document.Pages[0];
            await WaitUntilAsync(
                () => Math.Abs(page.RenderedScale - 2.5) < 0.01,
                TimeSpan.FromSeconds(5));
            Assert.Equal(2.5, page.RenderedScale, 3);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ChangingScalingDoesNotClearTheVisibleBitmap()
    {
        using var service = new FakePdfDocumentService(pageCount: 2);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        service.AllowRenders();

        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1000, Height = 800, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();

            PdfPageViewModel page = document.Pages[0];
            await WaitUntilAsync(() => page.Image is not null, TimeSpan.FromSeconds(5));
            var bitmap = page.Image;

            document.RenderScaling = 2.0;

            // The old bitmap is stretched until the sharper one arrives.
            Assert.Same(bitmap, page.Image);
            Assert.False(page.IsPlaceholderVisible);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Mimics the platform notifying the window that it moved to a monitor with
    /// a different DPI.  The headless window implementation is internal to
    /// Avalonia.Headless, so reflection is the only way to drive it.
    /// </summary>
    private static void SimulateMonitorScaling(Window window, double scaling)
    {
        object impl = window.PlatformImpl!;
        Type type = impl.GetType();
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;

        type.GetProperty("RenderScaling", flags)!.SetValue(impl, scaling);
        var changed = (Action<double>?)type.GetProperty("ScalingChanged", flags)!.GetValue(impl);
        changed?.Invoke(scaling);
    }

}
