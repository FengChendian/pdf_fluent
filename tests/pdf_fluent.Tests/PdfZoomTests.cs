using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using pdf_fluent.Controls;
using pdf_fluent.Services;
using pdf_fluent.ViewModels;
using pdf_fluent.Views;
using Xunit;

namespace pdf_fluent.Tests;

/// <summary>
/// Zoom has to be anchored at the mouse cursor (both axes) and has to animate
/// through real intermediate layouts without blanking the pages.
/// </summary>
public sealed class PdfZoomTests
{
    [AvaloniaFact]
    public async Task ZoomKeepsTheDocumentCentredWhileItFitsTheViewport()
    {
        using ZoomTestHarness ZoomTestHarness = await ZoomTestHarness.CreateAsync(windowWidth: 1000, windowHeight: 700);

        (PdfPageViewModel page, Rect before) = ZoomTestHarness.PageAt(0);

        // Off-centre on purpose: while the page is narrower than the viewport
        // there is no horizontal scroll room, so the document has to stay
        // centred instead of sliding away from the cursor.
        Point pointer = new(before.X + (before.Width * 0.2), before.Y + (before.Height * 0.4));
        double fractionY = (pointer.Y - before.Y) / before.Height;
        double centreX = ZoomTestHarness.Viewport.Width / 2;
        Assert.InRange(before.X + (before.Width / 2), centreX - 0.75, centreX + 0.75);

        ZoomTestHarness.Window.MouseWheel(
            ZoomTestHarness.ToWindow(pointer),
            new Vector(0, 1),
            RawInputModifiers.Control);
        ZoomTestHarness.Zoom.FinishAnimation();
        ZoomTestHarness.UpdateLayout();

        Assert.Equal(1.25, ZoomTestHarness.Document.Zoom, 6);
        Assert.True(
            ZoomTestHarness.Scroller.Extent.Width <= ZoomTestHarness.Scroller.Viewport.Width + 0.5,
            $"内容未占满视口（extent {ZoomTestHarness.Scroller.Extent.Width:F1} > viewport {ZoomTestHarness.Scroller.Viewport.Width:F1}），不应出现横向滚动条。");

        Rect after = ZoomTestHarness.PageRect(page.PageIndex);

        // Still centred horizontally ...
        Assert.InRange(after.X + (after.Width / 2), centreX - 0.75, centreX + 0.75);
        // ... while the vertical axis keeps following the cursor.
        Assert.InRange(after.Y + (fractionY * after.Height), pointer.Y - 0.75, pointer.Y + 0.75);
    }

    [AvaloniaFact]
    public async Task ZoomAcrossTheFillThresholdHasNoJumpOrGap()
    {
        using ZoomTestHarness ZoomTestHarness = await ZoomTestHarness.CreateAsync(windowWidth: 900, windowHeight: 700);

        (PdfPageViewModel page, Rect before) = ZoomTestHarness.PageAt(0);
        Assert.InRange(before.X, 74.5, 75.5);

        Point pointer = new(ZoomTestHarness.Viewport.Width * 0.75, before.Y + (before.Height * 0.4));
        double centreX = ZoomTestHarness.Viewport.Width / 2;
        double fillZoom = (ZoomTestHarness.Viewport.Width - 48) / before.Width;
        Assert.InRange(fillZoom, 1.13, 1.14);

        Rect? previous = null;
        double previousZoom = 0;
        void Walk(IEnumerable<double> zooms)
        {
            foreach (double zoom in zooms)
            {
                ZoomTestHarness.Zoom.ZoomTo(zoom, pointer, animate: false);
                ZoomTestHarness.UpdateLayout();

                Rect rect = ZoomTestHarness.PageRect(page.PageIndex);
                if (previous is { } prev)
                {
                    // Any jump here would appear as a blank band (gap) between
                    // the page edge and the viewport edge.
                    double widthChange = Math.Abs(rect.Width - prev.Width);
                    Assert.True(
                        Math.Abs(rect.X - prev.X) <= widthChange + 1.5,
                        $"缩放 {previousZoom:F3} -> {zoom:F3} 时页面横向跳动：{prev.X:F2} -> {rect.X:F2}。");
                }

                if (zoom < fillZoom - 0.0005)
                {
                    Assert.InRange(rect.X + (rect.Width / 2), centreX - 0.75, centreX + 0.75);
                }
                else
                {
                    Assert.True(
                        rect.X <= 24.5,
                        $"占满宽度后页面左侧出现空隙：x={rect.X:F2}。");
                }

                previous = rect;
                previousZoom = zoom;
            }
        }

        double[] zooms = Enumerable.Range(0, 22)
            .Select(step => fillZoom - 0.011 + (step * 0.001))
            .ToArray();

        Walk(zooms);
        Walk(zooms.Reverse());
    }

    [AvaloniaFact]
    public async Task ZoomAnimationKeepsPointerPositionOnEveryFrame()
    {
        using ZoomTestHarness ZoomTestHarness = await ZoomTestHarness.CreateAsync(windowWidth: 1000, windowHeight: 700);

        (PdfPageViewModel page, Rect before) = ZoomTestHarness.PageAt(0);
        Point pointer = new(before.X + (before.Width * 0.5), before.Y + (before.Height * 0.4));
        double fractionX = (pointer.X - before.X) / before.Width;
        double fractionY = (pointer.Y - before.Y) / before.Height;

        ZoomTestHarness.Zoom.ZoomTo(1.25, pointer);
        ZoomTestHarness.UpdateLayout();

        Rect initial = ZoomTestHarness.PageRect(page.PageIndex);
        AssertPointNear(pointer, new Point(
            initial.X + (fractionX * initial.Width),
            initial.Y + (fractionY * initial.Height)));

        double previousZoom = ZoomTestHarness.Document.VisualZoom;
        for (int frame = 0; frame < 6; frame++)
        {
            ZoomTestHarness.Zoom.Advance(TimeSpan.FromMilliseconds(30));
            ZoomTestHarness.UpdateLayout();

            double zoom = ZoomTestHarness.Document.VisualZoom;
            Assert.True(zoom > previousZoom, $"第 {frame} 帧没有继续放大：{zoom}。");
            Assert.True(zoom < 1.25, $"第 {frame} 帧越过了目标缩放：{zoom}。");
            previousZoom = zoom;

            Rect current = ZoomTestHarness.PageRect(page.PageIndex);
            AssertPointNear(pointer, new Point(
                current.X + (fractionX * current.Width),
                current.Y + (fractionY * current.Height)));
        }

        Assert.True(ZoomTestHarness.Zoom.IsAnimating, "动画在 210ms 之前就结束了。");

        ZoomTestHarness.Zoom.FinishAnimation();
        ZoomTestHarness.UpdateLayout();
        Assert.Equal(1.25, ZoomTestHarness.Document.Zoom, 6);
        Assert.False(ZoomTestHarness.Zoom.IsAnimating);

        Rect settled = ZoomTestHarness.PageRect(page.PageIndex);
        AssertPointNear(pointer, new Point(
            settled.X + (fractionX * settled.Width),
            settled.Y + (fractionY * settled.Height)));
    }

    [AvaloniaFact]
    public async Task ZoomAnimationFramesDoNotChangeLayoutOrRealizedPages()
    {
        using ZoomTestHarness harness = await ZoomTestHarness.CreateAsync();
        harness.Scroller.Offset = new Vector(0, 500);
        harness.UpdateLayout();
        var pointer = new Point(500, 320);
        (PdfPageViewModel page, Rect before) = harness.PageAt(pointer);
        double fractionX = (pointer.X - before.X) / before.Width;
        double fractionY = (pointer.Y - before.Y) / before.Height;
        int initialRealizedCount = harness.Items.GetRealizedContainers().Count();

        harness.Zoom.ZoomTo(0.5, pointer);
        harness.UpdateLayout();

        int expandedRealizedCount = harness.Items.GetRealizedContainers().Count();
        Assert.True(expandedRealizedCount > initialRealizedCount);
        double committedZoom = harness.Document.Zoom;
        Vector committedOffset = harness.Scroller.Offset;
        var panel = harness.Window.GetVisualDescendants().OfType<PdfVirtualizingPanel>().First();
        ITransform transform = panel.RenderTransform!;
        int measurePasses = panel.MeasurePassCount;

        harness.Zoom.Advance(TimeSpan.FromMilliseconds(30));
        harness.UpdateLayout();

        Assert.Equal(measurePasses, panel.MeasurePassCount);
        Assert.Equal(committedZoom, harness.Document.Zoom);
        Assert.Equal(committedOffset, harness.Scroller.Offset);
        Assert.Equal(expandedRealizedCount, harness.Items.GetRealizedContainers().Count());
        Assert.Same(transform, panel.RenderTransform);
        Assert.InRange(harness.Document.VisualZoom, 0.5, 1.0);
        Assert.Equal(page.PageIndex, harness.PageAt(pointer).Page.PageIndex);
        Rect current = harness.PageRect(page.PageIndex);
        AssertPointNear(pointer, new Point(
            current.X + current.Width * fractionX,
            current.Y + current.Height * fractionY));
    }

    [AvaloniaFact]
    public async Task ZoomPreservesPointerPositionWhenContentScrollsInBothAxes()
    {
        using ZoomTestHarness ZoomTestHarness = await ZoomTestHarness.CreateAsync(windowWidth: 900, windowHeight: 700);

        ZoomTestHarness.SetZoomImmediately(1.5);
        ZoomTestHarness.UpdateLayout();
        ZoomTestHarness.Scroller.Offset = new Vector(120, 900);
        ZoomTestHarness.UpdateLayout();

        (PdfPageViewModel page, Rect before) = ZoomTestHarness.PageAt(new Point(450, 320));
        Point pointer = new(450, 320);
        double fractionX = (pointer.X - before.X) / before.Width;
        double fractionY = (pointer.Y - before.Y) / before.Height;

        ZoomTestHarness.Zoom.ZoomBy(-0.25, pointer);
        ZoomTestHarness.Zoom.FinishAnimation();
        ZoomTestHarness.UpdateLayout();

        Assert.Equal(1.25, ZoomTestHarness.Document.Zoom, 6);

        Rect after = ZoomTestHarness.PageRect(page.PageIndex);
        AssertPointNear(pointer, new Point(
            after.X + (fractionX * after.Width),
            after.Y + (fractionY * after.Height)));
    }

    [AvaloniaFact]
    public async Task ScrollingToAnOffscreenPageAfterZoomUsesCurrentGeometry()
    {
        using ZoomTestHarness harness = await ZoomTestHarness.CreateAsync(pageCount: 60);
        const int pageIndex = 20;
        const double zoom = 0.5;

        harness.SetZoomImmediately(zoom);
        harness.UpdateLayout();

        double offsetY = 8 + harness.Document.Pages.Take(pageIndex)
            .Sum(page => PdfPageViewModel.ItemHeightAt(page, zoom));
        harness.Scroller.Offset = new Vector(0, offsetY);
        harness.UpdateLayout();

        Rect page = harness.PageRect(pageIndex);
        Assert.InRange(page.Y, -0.5, 0.5);
    }

    [AvaloniaFact]
    public async Task ToolbarButtonsZoomAroundTheViewportCenter()
    {
        using ZoomTestHarness ZoomTestHarness = await ZoomTestHarness.CreateAsync(windowWidth: 900, windowHeight: 700);

        Point center = new(ZoomTestHarness.Viewport.Width / 2, ZoomTestHarness.Viewport.Height / 2);
        (PdfPageViewModel page, Rect before) = ZoomTestHarness.PageAt(center);
        double fractionX = (center.X - before.X) / before.Width;
        double fractionY = (center.Y - before.Y) / before.Height;

        Button zoomIn = ZoomTestHarness.Window.GetVisualDescendants()
            .OfType<Button>()
            .First(button => button.Content is "+");
        Point buttonCenter = zoomIn.TranslatePoint(
            new Point(zoomIn.Bounds.Width / 2, zoomIn.Bounds.Height / 2),
            ZoomTestHarness.Window)!.Value;
        ZoomTestHarness.Window.MouseDown(buttonCenter, MouseButton.Left);
        ZoomTestHarness.Window.MouseUp(buttonCenter, MouseButton.Left);

        ZoomTestHarness.Zoom.FinishAnimation();
        ZoomTestHarness.UpdateLayout();

        Assert.Equal(1.25, ZoomTestHarness.Document.Zoom, 6);

        Rect after = ZoomTestHarness.PageRect(page.PageIndex);
        AssertPointNear(center, new Point(
            after.X + (fractionX * after.Width),
            after.Y + (fractionY * after.Height)));
    }

    [AvaloniaFact]
    public async Task ZoomKeepsTheCurrentBitmapUntilTheReRenderCompletes()
    {
        using ZoomTestHarness ZoomTestHarness = await ZoomTestHarness.CreateAsync(pageCount: 3);
        ZoomTestHarness.Service.AllowRenders();

        PdfPageViewModel page = ZoomTestHarness.Document.Pages[0];
        await ZoomTestHarness.WaitForAsync(() => page.Image is not null);
        WriteableBitmap bitmap = page.Image!;
        Assert.False(page.IsPlaceholderVisible);
        Assert.False(ZoomTestHarness.PlaceholderVisible(page.PageIndex));

        ZoomTestHarness.Zoom.ZoomBy(0.25, anchorViewportPoint: null);

        // A zoom step must never blank the page: the previous render stays on
        // screen (stretched) while the document animates to the new zoom.
        Assert.Same(bitmap, page.Image);
        Assert.False(page.IsPlaceholderVisible);
        Assert.False(ZoomTestHarness.PlaceholderVisible(page.PageIndex));

        ZoomTestHarness.Zoom.Advance(TimeSpan.FromMilliseconds(80));
        ZoomTestHarness.UpdateLayout();
        Assert.Same(bitmap, page.Image);
        Assert.False(page.IsPlaceholderVisible);

        ZoomTestHarness.Zoom.FinishAnimation();
        Assert.Same(bitmap, page.Image);

        await ZoomTestHarness.WaitForAsync(() => Math.Abs(page.RenderedScale - 1.5625) < 0.01);
        Assert.NotNull(page.Image);
        Assert.Equal(1.5625, page.RenderedScale, 3);
    }

    [AvaloniaFact]
    public async Task RepeatedWheelNotchesAccumulateWithoutLosingZoomSteps()
    {
        using ZoomTestHarness ZoomTestHarness = await ZoomTestHarness.CreateAsync(windowWidth: 1000, windowHeight: 700);

        (PdfPageViewModel page, Rect before) = ZoomTestHarness.PageAt(0);
        Point pointer = new(before.X + (before.Width * 0.5), before.Y + (before.Height * 0.4));
        double fractionX = (pointer.X - before.X) / before.Width;
        double fractionY = (pointer.Y - before.Y) / before.Height;
        Point windowPoint = ZoomTestHarness.ToWindow(pointer);

        void AssertAnchor()
        {
            Rect current = ZoomTestHarness.PageRect(page.PageIndex);
            AssertPointNear(pointer, new Point(
                current.X + (fractionX * current.Width),
                current.Y + (fractionY * current.Height)));
        }

        for (int i = 0; i < 3; i++)
        {
            ZoomTestHarness.Window.MouseWheel(windowPoint, new Vector(0, 1), RawInputModifiers.Control);
            ZoomTestHarness.UpdateLayout();
            AssertAnchor();
            ZoomTestHarness.Zoom.Advance(TimeSpan.FromMilliseconds(40));
            ZoomTestHarness.UpdateLayout();
            AssertAnchor();
        }

        ZoomTestHarness.Zoom.FinishAnimation();
        ZoomTestHarness.UpdateLayout();

        Assert.Equal(1.75, ZoomTestHarness.Document.Zoom, 6);

        Rect after = ZoomTestHarness.PageRect(page.PageIndex);
        AssertPointNear(pointer, new Point(
            after.X + (fractionX * after.Width),
            after.Y + (fractionY * after.Height)));
    }

    [AvaloniaFact]
    public async Task ZoomAnimationIsDrivenByTheRenderTimer()
    {
        using ZoomTestHarness ZoomTestHarness = await ZoomTestHarness.CreateAsync(windowWidth: 1000, windowHeight: 700);

        (PdfPageViewModel page, Rect before) = ZoomTestHarness.PageAt(0);
        Point pointer = new(before.X + (before.Width * 0.5), before.Y + (before.Height * 0.4));
        double fractionX = (pointer.X - before.X) / before.Width;
        double fractionY = (pointer.Y - before.Y) / before.Height;

        ZoomTestHarness.Zoom.ZoomTo(1.25, pointer);
        Assert.True(ZoomTestHarness.Zoom.IsAnimating);

        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (ZoomTestHarness.Zoom.IsAnimating && DateTime.UtcNow < deadline)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(16);
        }

        Assert.False(ZoomTestHarness.Zoom.IsAnimating, "缩放动画没有被渲染帧驱动完成。");
        ZoomTestHarness.UpdateLayout();
        Assert.Equal(1.25, ZoomTestHarness.Document.Zoom, 6);

        Rect after = ZoomTestHarness.PageRect(page.PageIndex);
        AssertPointNear(pointer, new Point(
            after.X + (fractionX * after.Width),
            after.Y + (fractionY * after.Height)));
    }

    [AvaloniaFact]
    public async Task RealPdfiumReRendersAtTheNewZoomAfterAZoomGesture()
    {
        string assetPath = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "assets", "CUDA_by_Example.pdf");
        Assert.True(File.Exists(assetPath), $"缺少测试样本：{Path.GetFullPath(assetPath)}");

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
            int widthAtOneHundredPercent = page.Image!.PixelSize.Width;
            Assert.Equal(1.25, page.RenderedScale, 3);

            view.EnsureZoomController();
            view.ZoomController!.ZoomBy(0.25, anchorViewportPoint: null);
            view.ZoomController.FinishAnimation();
            window.UpdateLayout();

            // The previous bitmap stays visible while the page re-renders.
            Assert.NotNull(page.Image);
            await WaitUntilAsync(
                () => Math.Abs(page.RenderedScale - 1.5625) < 0.01,
                TimeSpan.FromSeconds(30));
            Assert.True(
                page.Image!.PixelSize.Width > widthAtOneHundredPercent,
                $"重新渲染没有使用新的缩放比例：{widthAtOneHundredPercent} -> {page.Image.PixelSize.Width}。");

            var sharper = page.Image;
            for (int i = 0; i < 2; i++)
            {
                view.ZoomController.ZoomBy(-0.25, anchorViewportPoint: null);
                view.ZoomController.FinishAnimation();
                window.UpdateLayout();

                Assert.Same(sharper, page.Image);
                Assert.Equal(1.5625, page.RenderedScale, 3);
            }

            Assert.Equal(0.75, document.Zoom, 6);
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("等待页面渲染超时。");
            }

            await Task.Delay(10);
        }
    }

    private static void AssertPointNear(Point expected, Point actual, double tolerance = 0.75)
    {
        Assert.True(
            Math.Abs(expected.X - actual.X) <= tolerance &&
            Math.Abs(expected.Y - actual.Y) <= tolerance,
            $"鼠标指针位置发生偏移：期望 ({expected.X:F2}, {expected.Y:F2})，实际 ({actual.X:F2}, {actual.Y:F2})。");
    }
}
