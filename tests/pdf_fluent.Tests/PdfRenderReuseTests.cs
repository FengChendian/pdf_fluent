using Avalonia.Headless.XUnit;
using pdf_fluent.ViewModels;
using Xunit;

namespace pdf_fluent.Tests;

/// <summary>
/// Every page render costs a UI-thread copy of the raw bitmap (tens of
/// megabytes per page on a HiDPI screen), so the viewer must reuse a bitmap
/// that is already sharp enough instead of re-rendering on every zoom step.
/// Zooming back out, and going back to a level that was just displayed, must
/// cost no render at all.
/// </summary>
public sealed class PdfRenderReuseTests
{
    [AvaloniaFact]
    public async Task ZoomingBackOutKeepsTheSharperBitmap()
    {
        using ZoomTestHarness harness = await ZoomTestHarness.CreateAsync(pageCount: 4);
        harness.Service.AllowRenders();

        PdfPageViewModel page = harness.Document.Pages[0];
        await harness.WaitForAsync(() => page.Image is not null);
        Assert.Equal(1.25, page.RenderedScale, 3);

        harness.Zoom.ZoomBy(0.25, anchorViewportPoint: null);
        harness.Zoom.FinishAnimation();
        await harness.WaitForAsync(() => Math.Abs(page.RenderedScale - 1.5625) < 0.01);
        var sharper = page.Image;
        int rendersAfterZoomIn = harness.RenderCountFor(page.PageIndex);

        harness.Zoom.ZoomBy(-0.25, anchorViewportPoint: null);
        harness.Zoom.FinishAnimation();
        harness.UpdateLayout();

        Assert.Equal(1.0, harness.Document.Zoom, 6);

        // 1.5625 px/pt is still sharper than the 1.25 px/pt that 100% needs,
        // so the bitmap is reused (and downscaled by the compositor) instead
        // of paying for another render and bitmap upload.
        Assert.Same(sharper, page.Image);
        Assert.Equal(1.5625, page.RenderedScale, 3);
        Assert.Equal(rendersAfterZoomIn, harness.RenderCountFor(page.PageIndex));
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
    public async Task ZoomingOutFromOneHundredPercentKeepsTheSharperBitmap()
    {
        using ZoomTestHarness harness = await ZoomTestHarness.CreateAsync(pageCount: 4);
        harness.Service.AllowRenders();

        PdfPageViewModel page = harness.Document.Pages[0];
        await harness.WaitForAsync(() => page.Image is not null);
        var sharper = page.Image;
        int renders = harness.RenderCountFor(page.PageIndex);

        harness.Zoom.ZoomBy(-0.25, anchorViewportPoint: null);
        harness.Zoom.FinishAnimation();
        harness.UpdateLayout();

        Assert.Equal(0.75, harness.Document.Zoom, 6);
        Assert.Same(sharper, page.Image);
        Assert.Equal(1.25, page.RenderedScale, 3);
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

    [AvaloniaFact]
    public async Task ZoomingOutFarEnoughStillKeepsTheSharperBitmap()
    {
        using ZoomTestHarness harness = await ZoomTestHarness.CreateAsync(pageCount: 4);
        harness.Service.AllowRenders();

        PdfPageViewModel page = harness.Document.Pages[0];
        await harness.WaitForAsync(() => page.Image is not null);

        harness.Zoom.ZoomTo(1.5, new Avalonia.Point(400, 300));
        harness.Zoom.FinishAnimation();
        await harness.WaitForAsync(() => Math.Abs(page.RenderedScale - 1.875) < 0.01);
        var sharper = page.Image;
        int renders = harness.RenderCountFor(page.PageIndex);

        harness.Zoom.ZoomTo(0.5, new Avalonia.Point(400, 300));
        harness.Zoom.FinishAnimation();
        harness.UpdateLayout();

        Assert.Same(sharper, page.Image);
        Assert.Equal(1.875, page.RenderedScale, 3);
        Assert.Equal(renders, harness.RenderCountFor(page.PageIndex));
    }
}
