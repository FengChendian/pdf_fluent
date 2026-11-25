using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Skia;
using Avalonia.VisualTree;
using pdf_fluent.Controls;
using pdf_fluent.Models;
using pdf_fluent.Services;
using pdf_fluent.ViewModels;
using pdf_fluent.Views;
using SkiaSharp;
using Xunit;
using static pdf_fluent.Tests.PdfTestHelpers;

namespace pdf_fluent.Tests;

public sealed class PdfPageSharpnessTests
{
    [AvaloniaTheory]
    [InlineData(1.0, 1.0)]
    [InlineData(1.25, 1.0)]
    [InlineData(1.5, 1.0)]
    [InlineData(2.0, 1.0)]
    [InlineData(1.25, 1.17)]
    public async Task NativePagePixelsMapExactlyToScreenPixelsAfterScrolling(double dpi, double zoom)
    {
        using var service = new PDFiumService();
        using var document = new DocumentTabViewModel(SamplePdfPath(), service) { Zoom = zoom };
        await document.LoadAsync();
        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1001, Height = 800, Content = view };
        try
        {
            window.Show();
            SetDpi(window, dpi);
            window.UpdateLayout();
            Named<ItemsControl>(view, "PageItems").ScrollIntoView(10);
            window.UpdateLayout();
            PdfPageViewModel page = document.Pages[10];
            await WaitUntilAsync(() => page.Image is not null && Math.Abs(page.RenderedScale - 1.25 * zoom * dpi) < 1e-6);
            window.UpdateLayout();
            PdfPageImage image = PageImage(view, page);
            double initialOffset = view.DocumentScroller.Offset.Y;
            foreach (double offset in new[] { 0.0, 0.25, 0.5, 1.125 })
            {
                view.DocumentScroller.Offset = new Vector(0, initialOffset + offset);
                window.UpdateLayout();
                PdfPageGeometry geometry = image.Geometry;
                Assert.Equal(page.Image!.PixelSize.Width, geometry.Bounds.Width * dpi, 7);
                Assert.Equal(page.Image.PixelSize.Height, geometry.Bounds.Height * dpi, 7);
                Point origin = image.TranslatePoint(geometry.Bounds.Position, window)!.Value;
                Assert.Equal(Math.Round(origin.X * dpi), origin.X * dpi, 7);
                Assert.Equal(Math.Round(origin.Y * dpi), origin.Y * dpi, 7);
                Assert.Equal(BitmapInterpolationMode.None, image.InterpolationMode);
                var overlay = image.GetVisualParent()!.GetVisualChildren().OfType<PdfPageOverlay>().Single();
                Assert.Same(image, overlay.PageImage);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task CachedDownscaledPagesUseMediumFilteringAndAnimationUsesLinearFiltering()
    {
        using var service = new FakePdfDocumentService(1, useRequestedPixelSize: true);
        service.AllowRenders();
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1000, Height = 700, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            PdfPageViewModel page = document.Pages[0];
            await WaitUntilAsync(() => page.Image is not null);
            PdfPageImage image = PageImage(view, page);
            Assert.Equal(BitmapInterpolationMode.None, image.InterpolationMode);
            view.EnsureZoomController();
            var zoom = view.ZoomController!;
            zoom.ZoomTo(1.25, null);
            Assert.True(document.IsZoomAnimating);
            Assert.Equal(BitmapInterpolationMode.LowQuality, image.InterpolationMode);
            zoom.FinishAnimation();
            await WaitUntilAsync(() => Math.Abs(page.RenderedScale - 1.5625) < 1e-6);
            Assert.Equal(BitmapInterpolationMode.None, image.InterpolationMode);
            var sharper = page.Image;
            int renders = service.RenderCallCount;
            zoom.ZoomTo(1.0, null);
            Assert.Equal(BitmapInterpolationMode.LowQuality, image.InterpolationMode);
            zoom.FinishAnimation();
            window.UpdateLayout();
            Assert.Same(sharper, page.Image);
            Assert.Equal(renders, service.RenderCallCount);
            Assert.Equal(BitmapInterpolationMode.MediumQuality, image.InterpolationMode);
            // Even a same-size bitmap must not use nearest-neighbour while a
            // composition animation is scaling it outside the layout tree.
            zoom.ZoomTo(1.25, null);
            Assert.Equal(image.Geometry.PixelSize, page.Image!.PixelSize);
            Assert.Equal(BitmapInterpolationMode.LowQuality, image.InterpolationMode);
            zoom.FinishAnimation();
            Assert.Equal(BitmapInterpolationMode.None, image.InterpolationMode);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SmallZoomChangesThatCrossPixelBoundariesStillRenderTheRequiredSize()
    {
        using var service = new FakePdfDocumentService(1, useRequestedPixelSize: true);
        service.AllowRenders();
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        PdfPageViewModel page = document.Pages[0];
        document.EnsurePageRendered(page);
        await WaitUntilAsync(() => page.Image is not null);
        document.Zoom = 1.001;
        document.ResumeRendering();
        await WaitUntilAsync(() => page.Image!.PixelSize.Width == 751);
        Assert.Equal(1001, page.Image!.PixelSize.Height);
    }

    [AvaloniaTheory]
    [InlineData(1.25, 1.17)]
    [InlineData(1.5, 1.11)]
    public async Task PixelAlignedOverlayAndPointerSelectionUseTheSameCoordinates(double dpi, double zoom)
    {
        using var service = new FakePdfDocumentService(1);
        using var document = new DocumentTabViewModel("unused.pdf", service) { Zoom = zoom };
        await document.LoadAsync();
        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1001, Height = 800, Content = view };
        try
        {
            window.Show();
            SetDpi(window, dpi);
            window.UpdateLayout();
            PdfPageViewModel page = document.Pages[0];
            await WaitUntilAsync(() => page.Text is not null);
            window.UpdateLayout();
            PdfPageImage image = PageImage(view, page);
            PdfPageOverlay overlay = view.GetVisualDescendants().OfType<PdfPageOverlay>().Single();
            PdfRect character = page.Text!.Characters[0].Bounds;
            Rect bounds = overlay.GetLayoutBounds(character);
            Point pointer = image.TranslatePoint(new Point(bounds.X + bounds.Width * 0.2, bounds.Center.Y), window)!.Value;
            window.MouseMove(pointer);
            Assert.True(view.IsTextCursor);
            window.MouseDown(pointer, MouseButton.Left);
            Point end = image.TranslatePoint(new Point(bounds.X + bounds.Width * 0.8, bounds.Center.Y), window)!.Value;
            window.MouseMove(end);
            window.MouseUp(end, MouseButton.Left);
            Assert.Equal("P", await document.GetSelectedTextAsync());
            double scale = DocumentTabViewModel.LayoutScale * zoom;
            Point logical = image.Geometry.ToLogical(bounds.Center);
            Assert.Equal(character.CenterX * scale, logical.X, 7);
            Assert.Equal((page.Text.CropBox.T - character.CenterY) * scale, logical.Y, 7);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task NativePixelEdgesAreDrawnWithoutIntroducingIntermediateGrayPixels()
    {
        // Use real Skia rasterization in a local scope; the remaining Headless
        // suite keeps its lightweight drawing backend.
        // The locator scope is omitted from Avalonia's public reference API.
        using var scope = (IDisposable)typeof(AvaloniaLocator).GetMethod("EnterScope")!.Invoke(null, null)!;
        SkiaPlatform.Initialize();
        var page = new PdfPageViewModel(0, 25.4, 32.4, 1);
        var bitmap = new WriteableBitmap(new PixelSize(32, 41), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        try
        {
            using (var frame = bitmap.Lock())
            {
                var pixels = new byte[frame.RowBytes * 41];
                for (int y = 0; y < 41; y++)
                for (int x = 0; x < 32; x++)
                {
                    int offset = y * frame.RowBytes + x * 4;
                    byte value = x % 2 == 0 ? (byte)0 : (byte)255;
                    pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = value;
                    pixels[offset + 3] = 255;
                }
                Marshal.Copy(pixels, 0, frame.Address, pixels.Length);
            }
            page.SetImage(bitmap, 1.25);
            // The layout remains fractional and the bitmap's aspect ratio is
            // slightly different after rounding each edge to whole pixels.
            var control = new PdfPageImage { Page = page, Width = page.PageWidth, Height = page.PageHeight, UseLayoutRounding = false };
            control.Measure(new Size(40, 50));
            control.Arrange(new Rect(0, 0, page.PageWidth, page.PageHeight));
            using var surface = SKSurface.Create(new SKImageInfo(40, 50));
            surface.Canvas.Clear(SKColors.Magenta);
            await Avalonia.Skia.Helpers.DrawingContextHelper.RenderAsync(surface.Canvas, control);
            using var snapshot = surface.Snapshot();
            using var result = SKBitmap.FromImage(snapshot);
            for (int x = 3; x < 29; x++)
                Assert.Equal(x % 2 == 0 ? SKColors.Black : SKColors.White, result.GetPixel(x, 16));
        }
        finally { page.ClearImage(); }
    }

    private static PdfPageImage PageImage(Visual view, PdfPageViewModel page) =>
        view.GetVisualDescendants().OfType<PdfPageImage>().Single(image => ReferenceEquals(image.Page, page));

    private static void SetDpi(Window window, double scaling)
    {
        object impl = window.PlatformImpl!;
        impl.GetType().GetProperty("RenderScaling")!.SetValue(impl, scaling);
        ((Action<double>?)impl.GetType().GetProperty("ScalingChanged")!.GetValue(impl))?.Invoke(scaling);
    }
}
