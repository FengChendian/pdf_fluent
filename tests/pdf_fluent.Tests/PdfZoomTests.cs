using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.VisualTree;
using pdf_fluent.Controls;
using pdf_fluent.Services;
using pdf_fluent.ViewModels;
using pdf_fluent.Views;
using Xunit;
using static pdf_fluent.Tests.PdfTestHelpers;

namespace pdf_fluent.Tests;

/// <summary>
/// Zoom has to be anchored at the mouse cursor (both axes) and has to animate
/// through real intermediate states without blanking the pages.
///
/// <para>
/// The animation itself runs in the compositor: the controller commits the
/// target layout once and hands a scale animation
/// (<see cref="CompositionVisual.Scale"/> about
/// <see cref="CompositionVisual.CenterPoint"/>) to the render thread.  The
/// tests therefore check the committed layout, the composition state that is
/// handed over, and the logical zoom clock that samples the animation; page
/// rectangles in the animated state are reconstructed with
/// <see cref="ZoomTestHarness.VisualPageRect"/> when a visual position matters.
/// </para>
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
        Assert.Null(ZoomTestHarness.Panel.RenderTransform);

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
        using ZoomTestHarness harness = await ZoomTestHarness.CreateAsync(windowWidth: 1000, windowHeight: 700);

        (PdfPageViewModel page, Rect before) = harness.PageAt(0);
        Point pointer = new(before.X + (before.Width * 0.5), before.Y + (before.Height * 0.4));
        double fractionX = (pointer.X - before.X) / before.Width;
        double fractionY = (pointer.Y - before.Y) / before.Height;

        harness.Zoom.ZoomTo(1.25, pointer);
        harness.UpdateLayout();

        // The layout commit is compensated by a composition scale, not by a
        // UI-thread render transform.
        Assert.Null(harness.Panel.RenderTransform);
        Assert.True(harness.Zoom.HasCompositionAnimation, "缩放没有启动组合动画。");

        CompositionVisual visual = harness.PanelVisual;
        Assert.Equal(1 / 1.25, visual.Scale.X, 6);
        Assert.Equal(1 / 1.25, visual.Scale.Y, 6);
        Assert.True(
            Math.Abs(visual.CenterPoint.X) > 0.5 || Math.Abs(visual.CenterPoint.Y) > 0.5,
            "组合动画没有设置缩放原点。");

        // The values handed to the composition are exactly the ones the
        // controller uses for its own anchoring math.
        Assert.Equal(harness.Zoom.CurrentVisualScale, visual.Scale.X, 9);
        Assert.Equal(
            harness.Zoom.VisualOriginViewport.X + harness.Scroller.Offset.X,
            visual.CenterPoint.X,
            6);
        Assert.Equal(
            harness.Zoom.VisualOriginViewport.Y + harness.Scroller.Offset.Y,
            visual.CenterPoint.Y,
            6);

        // The composition handles the same pointer point the committed layout
        // was anchored at.  The horizontal axis is centred (the page fits), but
        // the pointer is on the page's centre line, which is the viewport centre.
        AssertPointNear(pointer, new Point(
            visual.CenterPoint.X - harness.Scroller.Offset.X,
            visual.CenterPoint.Y - harness.Scroller.Offset.Y));

        Rect initial = harness.PageRect(page.PageIndex);
        AssertPointNear(pointer, new Point(
            initial.X + (fractionX * initial.Width),
            initial.Y + (fractionY * initial.Height)));

        double baseScale = visual.Scale.X;
        double previousZoom = harness.Document.VisualZoom;
        for (int frame = 0; frame < 6; frame++)
        {
            harness.Zoom.Advance(TimeSpan.FromMilliseconds(30));
            harness.UpdateLayout();

            double zoom = harness.Document.VisualZoom;
            Assert.True(zoom > previousZoom, $"第 {frame} 帧没有继续放大：{zoom}。");
            Assert.True(zoom < 1.25, $"第 {frame} 帧越过了目标缩放：{zoom}。");
            previousZoom = zoom;

            // The UI thread advances the logical clock only; the composition
            // transform is interpolated by the render thread and its base value
            // is not rewritten per frame.
            Assert.Equal(baseScale, visual.Scale.X, 9);
            Assert.Null(harness.Panel.RenderTransform);

            // The logical clock and the scale handed to the composition agree.
            Assert.Equal(zoom / harness.Document.Zoom, harness.Zoom.CurrentVisualScale, 9);

            AssertPointNear(pointer, harness.VisualPoint(page.PageIndex, fractionX, fractionY));
        }

        Assert.True(harness.Zoom.IsAnimating, "动画在 210ms 之前就结束了。");

        harness.Zoom.FinishAnimation();
        harness.UpdateLayout();
        Assert.Equal(1.25, harness.Document.Zoom, 6);
        Assert.False(harness.Zoom.IsAnimating);
        Assert.False(harness.Zoom.HasCompositionAnimation);

        Rect settled = harness.PageRect(page.PageIndex);
        AssertPointNear(pointer, new Point(
            settled.X + (fractionX * settled.Width),
            settled.Y + (fractionY * settled.Height)));
    }

    [AvaloniaFact]
    public async Task ZoomStartsWithThePreviousVisualState()
    {
        using ZoomTestHarness harness = await ZoomTestHarness.CreateAsync(windowWidth: 1000, windowHeight: 700);

        (PdfPageViewModel page, Rect before) = harness.PageAt(0);
        Point pointer = new(before.X + (before.Width * 0.5), before.Y + (before.Height * 0.4));

        harness.Zoom.ZoomTo(1.25, pointer);
        harness.UpdateLayout();

        // The first visual frame must still look like the old zoom; the
        // compensation scale is exactly the ratio between the old and the
        // committed layout zoom.
        Rect visual = harness.VisualPageRect(page.PageIndex);
        AssertRectangleNear(before, visual, 0.75);

        Assert.Equal(1.0, harness.Document.VisualZoom, 6);
        Assert.True(harness.Zoom.HasCompositionAnimation);
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
        var panel = harness.Panel;
        int measurePasses = panel.MeasurePassCount;
        double compositionScale = harness.PanelVisual.Scale.X;
        Assert.Null(panel.RenderTransform);

        harness.Zoom.Advance(TimeSpan.FromMilliseconds(30));
        harness.UpdateLayout();

        Assert.Equal(measurePasses, panel.MeasurePassCount);
        Assert.Equal(committedZoom, harness.Document.Zoom);
        Assert.Equal(committedOffset, harness.Scroller.Offset);
        Assert.Equal(expandedRealizedCount, harness.Items.GetRealizedContainers().Count());
        Assert.Null(panel.RenderTransform);
        Assert.Equal(compositionScale, harness.PanelVisual.Scale.X, 9);
        Assert.InRange(harness.Document.VisualZoom, 0.5, 1.0);
        Assert.Equal(page.PageIndex, harness.PageAt(pointer).Page.PageIndex);
        AssertPointNear(pointer, harness.VisualPoint(page.PageIndex, fractionX, fractionY));
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

        double offsetY = harness.Panel.Padding.Top + harness.Document.Pages.Take(pageIndex)
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
            .First(button => button.Name == "ZoomInButton");
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

        bool AllRealizedPagesResolved() => ZoomTestHarness.Items.GetRealizedContainers()
            .Select(container => container.DataContext as PdfPageViewModel)
            .All(candidate => candidate is null ||
                              candidate.Image is not null ||
                              candidate.ErrorMessage is not null);

        await ZoomTestHarness.WaitForAsync(AllRealizedPagesResolved, 10000);
        WriteableBitmap bitmap = page.Image!;
        Assert.False(page.IsPlaceholderVisible);
        Assert.False(ZoomTestHarness.PlaceholderVisible(page.PageIndex));
        int rendersBefore = ZoomTestHarness.RenderCountFor(page.PageIndex);
        long cachedBytes = ZoomTestHarness.Document.CachedBitmapBytes;
        Assert.Equal(LiveBitmapBytes(ZoomTestHarness.Document), cachedBytes);

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
        Assert.True(ZoomTestHarness.Zoom.HasCompositionAnimation);

        // Bitmap work stays suspended for the whole animation: the composition
        // only transforms the existing draw list, so neither the bitmaps nor
        // the cache accounting change until the zoom settles.
        Assert.True(ZoomTestHarness.Document.IsRenderingSuspended);
        Assert.Equal(0, ZoomTestHarness.Document.PendingRenderCount);
        Assert.Equal(rendersBefore, ZoomTestHarness.RenderCountFor(page.PageIndex));
        Assert.Equal(cachedBytes, ZoomTestHarness.Document.CachedBitmapBytes);
        Assert.Same(bitmap, page.Image);

        ZoomTestHarness.Zoom.FinishAnimation();
        Assert.Same(bitmap, page.Image);

        await ZoomTestHarness.WaitForAsync(() => Math.Abs(page.RenderedScale - 1.5625) < 0.01);
        Assert.NotNull(page.Image);
        Assert.Equal(1.5625, page.RenderedScale, 3);
    }

    [AvaloniaFact]
    public async Task RepeatedWheelNotchesAccumulateWithoutLosingZoomSteps()
    {
        using ZoomTestHarness harness = await ZoomTestHarness.CreateAsync(windowWidth: 1000, windowHeight: 700);

        (PdfPageViewModel page, Rect before) = harness.PageAt(0);
        Point pointer = new(before.X + (before.Width * 0.5), before.Y + (before.Height * 0.4));
        double fractionX = (pointer.X - before.X) / before.Width;
        double fractionY = (pointer.Y - before.Y) / before.Height;
        Point windowPoint = harness.ToWindow(pointer);

        void AssertVisualAnchor()
        {
            AssertPointNear(pointer, harness.VisualPoint(page.PageIndex, fractionX, fractionY));
        }

        for (int i = 0; i < 3; i++)
        {
            harness.Window.MouseWheel(windowPoint, new Vector(0, 1), RawInputModifiers.Control);
            harness.UpdateLayout();
            AssertVisualAnchor();
            harness.Zoom.Advance(TimeSpan.FromMilliseconds(40));
            harness.UpdateLayout();
            AssertVisualAnchor();
        }

        harness.Zoom.FinishAnimation();
        harness.UpdateLayout();

        Assert.Equal(1.75, harness.Document.Zoom, 6);

        Rect after = harness.PageRect(page.PageIndex);
        AssertPointNear(pointer, new Point(
            after.X + (fractionX * after.Width),
            after.Y + (fractionY * after.Height)));
    }

    [AvaloniaFact]
    public async Task RetargetingMidAnimationKeepsTheVisualStateContinuous()
    {
        using ZoomTestHarness harness = await ZoomTestHarness.CreateAsync(windowWidth: 1000, windowHeight: 700);

        (PdfPageViewModel page, Rect before) = harness.PageAt(0);
        Point pointer = new(before.X + (before.Width * 0.5), before.Y + (before.Height * 0.4));
        double fractionX = (pointer.X - before.X) / before.Width;
        double fractionY = (pointer.Y - before.Y) / before.Height;

        harness.Zoom.ZoomTo(1.25, pointer);
        harness.UpdateLayout();
        harness.Zoom.Advance(TimeSpan.FromMilliseconds(90));
        harness.UpdateLayout();

        double displayedZoom = harness.Document.VisualZoom;
        Assert.InRange(displayedZoom, 1.001, 1.249);
        Rect midVisual = harness.VisualPageRect(page.PageIndex);
        Point displayedAnchor = harness.VisualPoint(page.PageIndex, fractionX, fractionY);
        AssertPointNear(pointer, displayedAnchor);

        // A new target in the middle of the animation must continue from the
        // displayed state instead of jumping back to the old layout.
        harness.Zoom.ZoomTo(1.5, pointer);
        harness.UpdateLayout();

        Assert.Equal(1.5, harness.Document.Zoom, 6);
        Assert.Equal(displayedZoom, harness.Document.VisualZoom, 6);
        Assert.True(harness.Zoom.HasCompositionAnimation);
        AssertPointNear(pointer, harness.VisualPoint(page.PageIndex, fractionX, fractionY));
        AssertRectangleNear(midVisual, harness.VisualPageRect(page.PageIndex), 1.0);

        harness.Zoom.Advance(TimeSpan.FromMilliseconds(60));
        harness.UpdateLayout();
        AssertPointNear(pointer, harness.VisualPoint(page.PageIndex, fractionX, fractionY));

        harness.Zoom.FinishAnimation();
        harness.UpdateLayout();
        Assert.Equal(1.5, harness.Document.Zoom, 6);
        Rect settled = harness.PageRect(page.PageIndex);
        AssertPointNear(pointer, new Point(
            settled.X + (fractionX * settled.Width),
            settled.Y + (fractionY * settled.Height)));
    }

    [AvaloniaFact]
    public async Task RapidRetargetingWhileTheAnimationIsCommittedDoesNotThrow()
    {
        using ZoomTestHarness harness = await ZoomTestHarness.CreateAsync(windowWidth: 1000, windowHeight: 700);

        (PdfPageViewModel page, Rect before) = harness.PageAt(0);
        Point pointer = new(before.X + (before.Width * 0.5), before.Y + (before.Height * 0.4));

        for (int i = 0; i < 6; i++)
        {
            harness.Zoom.ZoomTo(i % 2 == 0 ? 1.5 : 0.75, pointer);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(12);
        }

        harness.Zoom.FinishAnimation();
        harness.UpdateLayout();

        Assert.False(harness.Zoom.IsAnimating);
        Assert.False(harness.Zoom.HasCompositionAnimation);
        Assert.Equal(0.75, harness.Document.Zoom, 6);
        Assert.Equal(1.0, harness.PanelVisual.Scale.X, 9);
        Assert.Equal(1.0, harness.PanelVisual.Scale.Y, 9);
        Assert.Equal(0.0, harness.PanelVisual.CenterPoint.X, 9);
        Assert.Equal(0.0, harness.PanelVisual.CenterPoint.Y, 9);
        Assert.False(harness.Document.IsRenderingSuspended);
        Assert.Equal(harness.Document.Zoom, harness.Document.VisualZoom, 9);
        Assert.Null(harness.Panel.RenderTransform);
    }

    [AvaloniaFact]
    public async Task RetargetingToTheDisplayedZoomSettlesWithoutAnotherAnimation()
    {
        using ZoomTestHarness harness = await ZoomTestHarness.CreateAsync(windowWidth: 1000, windowHeight: 700);

        (PdfPageViewModel page, Rect before) = harness.PageAt(0);
        Point pointer = new(before.X + (before.Width * 0.5), before.Y + (before.Height * 0.4));

        harness.Zoom.ZoomTo(1.5, pointer);
        harness.UpdateLayout();
        harness.Zoom.Advance(TimeSpan.FromMilliseconds(60));
        harness.UpdateLayout();

        double displayed = harness.Document.VisualZoom;
        Assert.InRange(displayed, 1.001, 1.499);

        harness.Zoom.ZoomTo(displayed, pointer);
        harness.UpdateLayout();

        Assert.Equal(displayed, harness.Document.Zoom, 9);
        Assert.False(harness.Zoom.IsAnimating);
        Assert.False(harness.Zoom.HasCompositionAnimation);
        Assert.False(harness.Document.IsRenderingSuspended);
        Assert.Equal(displayed, harness.Document.VisualZoom, 9);
        Assert.Equal(1.0, harness.PanelVisual.Scale.X, 9);
        Assert.Equal(0.0, harness.PanelVisual.CenterPoint.X, 9);
        Assert.Null(harness.Panel.RenderTransform);
    }

    [AvaloniaFact]
    public async Task RapidAlternatingWheelNotchesKeepMovingThePage()
    {
        using ZoomTestHarness harness = await ZoomTestHarness.CreateAsync(windowWidth: 1000, windowHeight: 700);

        (PdfPageViewModel page, Rect before) = harness.PageAt(0);
        Point pointer = new(before.X + (before.Width * 0.5), before.Y + (before.Height * 0.4));
        double fractionX = (pointer.X - before.X) / before.Width;
        double fractionY = (pointer.Y - before.Y) / before.Height;

        double previous = harness.Document.VisualZoom;
        double min = previous;
        double max = previous;
        for (int i = 0; i < 12; i++)
        {
            int direction = i % 2 == 0 ? 1 : -1;
            harness.Zoom.ZoomBy(direction * 0.25, pointer);
            harness.UpdateLayout();
            harness.Zoom.Advance(TimeSpan.FromMilliseconds(10));
            harness.UpdateLayout();

            double visual = harness.Document.VisualZoom;
            if (direction > 0)
            {
                Assert.True(
                    visual > previous + 0.02,
                    $"第 {i} 次向上滚动没有继续放大：{previous:F4} -> {visual:F4}。");
            }
            else
            {
                Assert.True(
                    visual < previous - 0.02,
                    $"第 {i} 次向下滚动没有继续缩小：{previous:F4} -> {visual:F4}。");
            }

            previous = visual;
            min = Math.Min(min, visual);
            max = Math.Max(max, visual);
            Assert.True(harness.Zoom.HasCompositionAnimation);
            AssertPointNear(pointer, harness.VisualPoint(page.PageIndex, fractionX, fractionY));
        }

        // Rapidly reversing direction must still move the page visibly instead
        // of degenerating into a tiny oscillation around the midpoint.
        Assert.True(
            max - min > 0.15,
            $"快速来回滚动时缩放幅度过小：{min:F4} .. {max:F4}（跨度 {max - min:F4}）。");

        harness.Zoom.FinishAnimation();
        harness.UpdateLayout();

        // An even number of alternating notches returns to the original zoom.
        Assert.Equal(1.0, harness.Document.Zoom, 6);
        Assert.Equal(1.0, harness.Document.VisualZoom, 9);
        Assert.False(harness.Zoom.IsAnimating);
        Assert.Equal(1.0, harness.PanelVisual.Scale.X, 9);
        Assert.False(harness.Document.IsRenderingSuspended);
    }

    [AvaloniaFact]
    public async Task ReversingAfterRapidSameDirectionWheelNotchesRespondsImmediately()
    {
        using ZoomTestHarness harness = await ZoomTestHarness.CreateAsync(windowWidth: 1000, windowHeight: 700);

        (PdfPageViewModel page, Rect before) = harness.PageAt(0);
        Point pointer = new(before.X + (before.Width * 0.5), before.Y + (before.Height * 0.4));
        Point windowPoint = harness.ToWindow(pointer);

        for (int i = 0; i < 4; i++)
        {
            harness.Zoom.ZoomBy(0.25, pointer);
        }

        harness.Zoom.Advance(TimeSpan.FromMilliseconds(10));
        harness.UpdateLayout();
        double displayedBeforeReverse = harness.Document.VisualZoom;
        Assert.InRange(displayedBeforeReverse, 1.01, 1.99);

        harness.Window.MouseWheel(windowPoint, new Vector(0, -1), RawInputModifiers.Control);
        harness.UpdateLayout();

        Assert.True(
            harness.Document.Zoom < displayedBeforeReverse,
            $"反向滚动仍沿旧方向提交了目标：视觉 {displayedBeforeReverse:F4}，目标 {harness.Document.Zoom:F4}。");

        harness.Zoom.Advance(TimeSpan.FromMilliseconds(10));
        harness.UpdateLayout();
        Assert.True(
            harness.Document.VisualZoom < displayedBeforeReverse,
            $"反向滚动后视觉缩放没有立即缩小：{displayedBeforeReverse:F4} -> {harness.Document.VisualZoom:F4}。");

        harness.Zoom.FinishAnimation();
        harness.UpdateLayout();
        Assert.False(harness.Document.IsRenderingSuspended);
    }

    [AvaloniaFact]
    public async Task RapidRepeatedWheelNotchesKeepUpWithTheTarget()
    {
        using ZoomTestHarness harness = await ZoomTestHarness.CreateAsync(windowWidth: 1000, windowHeight: 700);

        (PdfPageViewModel page, Rect before) = harness.PageAt(0);
        Point pointer = new(before.X + (before.Width * 0.5), before.Y + (before.Height * 0.4));
        double fractionX = (pointer.X - before.X) / before.Width;
        double fractionY = (pointer.Y - before.Y) / before.Height;

        harness.Zoom.ZoomBy(0.25, pointer);
        Assert.Equal(210.0, harness.Zoom.CurrentSegmentDurationMs, 6);
        harness.Zoom.Advance(TimeSpan.FromMilliseconds(10));
        harness.UpdateLayout();
        double first = harness.Document.VisualZoom;

        harness.Zoom.ZoomBy(0.25, pointer);
        Assert.Equal(60.0, harness.Zoom.CurrentSegmentDurationMs, 6);
        harness.Zoom.Advance(TimeSpan.FromMilliseconds(10));
        harness.UpdateLayout();
        double second = harness.Document.VisualZoom;

        harness.Zoom.ZoomBy(0.25, pointer);
        Assert.Equal(60.0, harness.Zoom.CurrentSegmentDurationMs, 6);
        harness.Zoom.Advance(TimeSpan.FromMilliseconds(10));
        harness.UpdateLayout();
        double third = harness.Document.VisualZoom;

        // With a full 210 ms restart per notch the visual would fall several
        // steps behind; the catch-up animation keeps it close to the target.
        Assert.True(second > first + 0.2, $"连续放大时第二格滞后：{first:F4} -> {second:F4}。");
        Assert.True(third > second + 0.2, $"连续放大时第三格滞后：{second:F4} -> {third:F4}。");
        Assert.True(third > 1.6, $"连续三次滚轮后视觉缩放仍然落后：{third:F4}。");
        AssertPointNear(pointer, harness.VisualPoint(page.PageIndex, fractionX, fractionY));

        harness.Zoom.FinishAnimation();
        harness.UpdateLayout();
        Assert.Equal(1.75, harness.Document.Zoom, 6);
        Assert.False(harness.Zoom.IsAnimating);
        Assert.Equal(1.0, harness.PanelVisual.Scale.X, 9);
    }

    [AvaloniaFact]
    public async Task RetargetedAnimationUsesACatchUpDuration()
    {
        using ZoomTestHarness harness = await ZoomTestHarness.CreateAsync(windowWidth: 1000, windowHeight: 700);

        (PdfPageViewModel page, Rect before) = harness.PageAt(0);
        Point pointer = new(before.X + (before.Width * 0.5), before.Y + (before.Height * 0.4));

        harness.Zoom.ZoomTo(1.25, pointer);
        Assert.Equal(210.0, harness.Zoom.CurrentSegmentDurationMs, 6);
        harness.Zoom.Advance(TimeSpan.FromMilliseconds(30));
        harness.UpdateLayout();

        // 30 ms between two notches -> 30 * 1.2 = 36 ms, clamped to 60 ms.
        harness.Zoom.ZoomTo(1.5, pointer);
        Assert.Equal(60.0, harness.Zoom.CurrentSegmentDurationMs, 6);
        harness.Zoom.Advance(TimeSpan.FromMilliseconds(30));
        harness.UpdateLayout();
        Assert.True(harness.Zoom.IsAnimating, "追赶动画在 60ms 之前就结束了。");
        Assert.True(harness.Document.VisualZoom > 1.35, $"追赶动画没有及时接近目标。");

        harness.Zoom.Advance(TimeSpan.FromMilliseconds(31));
        harness.UpdateLayout();
        Assert.False(harness.Zoom.IsAnimating);
        Assert.Equal(1.5, harness.Document.Zoom, 6);
        Assert.Equal(1.0, harness.PanelVisual.Scale.X, 9);
        Assert.False(harness.Document.IsRenderingSuspended);

        // A slow retarget scales with the interval instead of always using the
        // shortest catch-up animation.
        harness.Zoom.ZoomTo(2.0, pointer);
        harness.Zoom.Advance(TimeSpan.FromMilliseconds(120));
        harness.Zoom.ZoomTo(2.25, pointer);
        Assert.Equal(144.0, harness.Zoom.CurrentSegmentDurationMs, 6);

        harness.Zoom.FinishAnimation();
        harness.UpdateLayout();
        Assert.Equal(2.25, harness.Document.Zoom, 6);
    }

    [AvaloniaFact]
    public async Task SettlingRemovesTheCompositionAnimationAndRestoresTheLayout()
    {
        using ZoomTestHarness harness = await ZoomTestHarness.CreateAsync(windowWidth: 1000, windowHeight: 700);

        (PdfPageViewModel page, Rect before) = harness.PageAt(0);
        Point pointer = new(before.X + (before.Width * 0.5), before.Y + (before.Height * 0.4));

        harness.Zoom.ZoomTo(1.5, pointer);
        harness.UpdateLayout();

        CompositionVisual visual = harness.PanelVisual;
        Assert.True(harness.Zoom.HasCompositionAnimation);
        Assert.Equal(1 / 1.5, visual.Scale.X, 6);
        Assert.True(harness.Document.IsRenderingSuspended);

        harness.Zoom.FinishAnimation();
        harness.UpdateLayout();

        Assert.False(harness.Zoom.IsAnimating);
        Assert.False(harness.Zoom.HasCompositionAnimation);
        Assert.Null(harness.Panel.RenderTransform);
        Assert.Equal(1.0, visual.Scale.X, 9);
        Assert.Equal(1.0, visual.Scale.Y, 9);
        Assert.Equal(0.0, visual.CenterPoint.X, 9);
        Assert.Equal(0.0, visual.CenterPoint.Y, 9);
        Assert.Equal(harness.Document.Zoom, harness.Document.VisualZoom, 9);
        Assert.False(harness.Document.IsRenderingSuspended);
        Assert.Equal(1.5, harness.Document.Zoom, 6);
    }

    [AvaloniaFact]
    public async Task AnimationClockStartsWithTheCompositionCommit()
    {
        using ZoomTestHarness harness = await ZoomTestHarness.CreateAsync(windowWidth: 1000, windowHeight: 700);

        (PdfPageViewModel page, Rect before) = harness.PageAt(0);
        Point pointer = new(before.X + (before.Width * 0.5), before.Y + (before.Height * 0.4));

        harness.Zoom.ZoomTo(1.25, pointer);
        Assert.True(harness.Zoom.IsAnimating);
        Assert.False(
            harness.Zoom.IsClockAlignedWithComposition,
            "逻辑时钟在组合提交之前就启动了。");

        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (!harness.Zoom.IsClockAlignedWithComposition && DateTime.UtcNow < deadline)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(5);
        }

        Assert.True(
            harness.Zoom.IsClockAlignedWithComposition,
            "组合提交后动画时钟仍未启动。");
        Assert.True(harness.Zoom.IsAnimating);

        harness.Zoom.FinishAnimation();
        harness.UpdateLayout();
        Assert.False(harness.Zoom.IsAnimating);
        Assert.False(harness.Zoom.HasCompositionAnimation);
    }

    [AvaloniaFact]
    public async Task DetachingMidAnimationStopsTheCompositionAnimation()
    {
        using ZoomTestHarness harness = await ZoomTestHarness.CreateAsync(windowWidth: 1000, windowHeight: 700);

        (PdfPageViewModel page, Rect before) = harness.PageAt(0);
        Point pointer = new(before.X + (before.Width * 0.5), before.Y + (before.Height * 0.4));

        harness.Zoom.ZoomTo(1.25, pointer);
        harness.UpdateLayout();
        harness.Zoom.Advance(TimeSpan.FromMilliseconds(60));
        Assert.True(harness.Document.IsRenderingSuspended);

        harness.Zoom.Detach();

        Assert.False(harness.Zoom.IsAnimating);
        Assert.False(harness.Zoom.HasCompositionAnimation);
        Assert.False(harness.Document.IsRenderingSuspended);
        Assert.Equal(harness.Document.Zoom, harness.Document.VisualZoom, 9);
        Assert.Equal(1.0, harness.PanelVisual.Scale.X, 9);
        Assert.Equal(0.0, harness.PanelVisual.CenterPoint.X, 9);
        Assert.Null(harness.Panel.RenderTransform);
    }

    [AvaloniaFact]
    public async Task ClosingTheWindowMidAnimationReleasesRendering()
    {
        ZoomTestHarness harness = await ZoomTestHarness.CreateAsync(windowWidth: 1000, windowHeight: 700);
        try
        {
            (PdfPageViewModel page, Rect before) = harness.PageAt(0);
            Point pointer = new(before.X + (before.Width * 0.5), before.Y + (before.Height * 0.4));
            PdfZoomController controller = harness.Zoom;

            controller.ZoomTo(1.25, pointer);
            controller.Advance(TimeSpan.FromMilliseconds(60));
            Assert.True(harness.Document.IsRenderingSuspended);

            harness.Window.Close();

            Assert.False(controller.IsAnimating);
            Assert.False(controller.HasCompositionAnimation);
            Assert.False(harness.Document.IsRenderingSuspended);
            Assert.Null(harness.View.ZoomController);
        }
        finally
        {
            harness.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task RemovingTheDocumentViewMidAnimationDetachesTheController()
    {
        using ZoomTestHarness harness = await ZoomTestHarness.CreateAsync(windowWidth: 1000, windowHeight: 700);

        (PdfPageViewModel page, Rect before) = harness.PageAt(0);
        Point pointer = new(before.X + (before.Width * 0.5), before.Y + (before.Height * 0.4));
        PdfZoomController controller = harness.Zoom;

        controller.ZoomTo(1.25, pointer);
        controller.Advance(TimeSpan.FromMilliseconds(50));
        Assert.True(harness.Document.IsRenderingSuspended);

        harness.Window.Content = null;
        harness.UpdateLayout();

        Assert.False(controller.IsAnimating);
        Assert.False(controller.HasCompositionAnimation);
        Assert.False(harness.Document.IsRenderingSuspended);
        Assert.Null(harness.View.ZoomController);
    }

    [AvaloniaFact]
    public async Task ZoomAnimationSettlesWithoutManualFrameAdvancing()
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

        Assert.False(ZoomTestHarness.Zoom.IsAnimating, "缩放动画没有被组合动画驱动完成。");
        ZoomTestHarness.UpdateLayout();
        Assert.Equal(1.25, ZoomTestHarness.Document.Zoom, 6);
        Assert.False(ZoomTestHarness.Zoom.HasCompositionAnimation);
        Assert.Equal(1.0, ZoomTestHarness.PanelVisual.Scale.X, 9);

        Rect after = ZoomTestHarness.PageRect(page.PageIndex);
        AssertPointNear(pointer, new Point(
            after.X + (fractionX * after.Width),
            after.Y + (fractionY * after.Height)));
    }

    [AvaloniaFact]
    public async Task RealPdfiumReRendersAtTheNewZoomAfterAZoomGesture()
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

    private static void AssertPointNear(Point expected, Point actual, double tolerance = 0.75)
    {
        Assert.True(
            Math.Abs(expected.X - actual.X) <= tolerance &&
            Math.Abs(expected.Y - actual.Y) <= tolerance,
            $"鼠标指针位置发生偏移：期望 ({expected.X:F2}, {expected.Y:F2})，实际 ({actual.X:F2}, {actual.Y:F2})。");
    }

    private static void AssertRectangleNear(Rect expected, Rect actual, double tolerance)
    {
        Assert.True(
            Math.Abs(expected.X - actual.X) <= tolerance &&
            Math.Abs(expected.Y - actual.Y) <= tolerance &&
            Math.Abs(expected.Width - actual.Width) <= tolerance &&
            Math.Abs(expected.Height - actual.Height) <= tolerance,
            $"页面矩形发生偏移：期望 {expected}，实际 {actual}。");
    }

}
