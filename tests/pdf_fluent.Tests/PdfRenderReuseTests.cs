using Avalonia.Headless.XUnit;
using pdf_fluent.ViewModels;
using Xunit;

namespace pdf_fluent.Tests;

/// <summary>
/// The viewer must reuse a bitmap that is already sharp enough instead of
/// re-rendering and uploading another bitmap on every zoom step.
/// Zooming back out, and going back to a level that was just displayed, must
/// cost no render at all.
/// </summary>
public sealed class PdfRenderReuseTests
{
    [AvaloniaTheory]
    [InlineData(1.25, 1.0, true)]
    [InlineData(1.0, 0.75, true)]
    [InlineData(1.5, 0.5, false)]
    public async Task ZoomingOutKeepsTheSharperBitmap(double initialZoom, double targetZoom, bool useRelativeZoom)
    {
        using ZoomTestHarness harness = await ZoomTestHarness.CreateAsync(pageCount: 4);
        harness.Service.AllowRenders();

        PdfPageViewModel page = harness.Document.Pages[0];
        await harness.WaitForAsync(() => page.Image is not null);
        Assert.Equal(1.25, page.RenderedScale, 3);

        Avalonia.Point? anchor = useRelativeZoom ? null : new Avalonia.Point(400, 300);
        void ChangeZoom(double target)
        {
            if (useRelativeZoom)
                harness.Zoom.ZoomBy(target - harness.Document.Zoom, anchor);
            else
                harness.Zoom.ZoomTo(target, anchor);
            harness.Zoom.FinishAnimation();
            harness.UpdateLayout();
        }

        if (initialZoom != harness.Document.Zoom)
            ChangeZoom(initialZoom);
        double renderedScale = DocumentTabViewModel.LayoutScale * initialZoom;
        await harness.WaitForAsync(() => Math.Abs(page.RenderedScale - renderedScale) < 0.01);
        var sharper = page.Image;
        int renders = harness.RenderCountFor(page.PageIndex);

        ChangeZoom(targetZoom);

        Assert.Equal(targetZoom, harness.Document.Zoom, 6);

        // Downscale the existing sharper bitmap without rendering or uploading another.
        Assert.Same(sharper, page.Image);
        Assert.Equal(renderedScale, page.RenderedScale, 3);
        Assert.Equal(renders, harness.RenderCountFor(page.PageIndex));
    }

    [AvaloniaFact]
    public async Task RepeatedBackAndForthZoomingRendersOnlyTheNewLevelOnce()
    {
        using ZoomTestHarness harness = await ZoomTestHarness.CreateAsync(pageCount: 4);
        harness.Service.AllowRenders();

        PdfPageViewModel page = harness.Document.Pages[0];
        await harness.WaitForAsync(() => page.Image is not null);

        harness.Zoom.ZoomBy(0.25, anchorViewportPoint: null);
        harness.Zoom.FinishAnimation();
        await harness.WaitForAsync(() => Math.Abs(page.RenderedScale - 1.5625) < 0.01);
        int renders = harness.RenderCountFor(page.PageIndex);

        for (int i = 0; i < 3; i++)
        {
            harness.Zoom.ZoomBy(-0.25, anchorViewportPoint: null);
            harness.Zoom.FinishAnimation();
            harness.UpdateLayout();

            harness.Zoom.ZoomBy(0.25, anchorViewportPoint: null);
            harness.Zoom.FinishAnimation();
            harness.UpdateLayout();
        }

        Assert.Equal(1.25, harness.Document.Zoom, 6);
        Assert.Equal(1.5625, page.RenderedScale, 3);
        Assert.Equal(renders, harness.RenderCountFor(page.PageIndex));
    }

    [AvaloniaFact]
    public async Task ZoomingInBeyondTheRenderedResolutionReRenders()
    {
        using ZoomTestHarness harness = await ZoomTestHarness.CreateAsync(pageCount: 4);
        harness.Service.AllowRenders();

        PdfPageViewModel page = harness.Document.Pages[0];
        await harness.WaitForAsync(() => page.Image is not null);
        int renders = harness.RenderCountFor(page.PageIndex);

        harness.Zoom.ZoomTo(1.5, new Avalonia.Point(400, 300));
        harness.Zoom.FinishAnimation();
        harness.UpdateLayout();

        await harness.WaitForAsync(() => Math.Abs(page.RenderedScale - 1.875) < 0.01);
        Assert.True(
            harness.RenderCountFor(page.PageIndex) > renders,
            "放大到超出当前位图分辨率时应该重新渲染。");
    }
}
