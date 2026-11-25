using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Skia;
using pdf_fluent.Controls;
using pdf_fluent.Services;
using pdf_fluent.ViewModels;
using SkiaSharp;
using Xunit;

namespace pdf_fluent.Tests;

public sealed class PdfPageTransparencyTests
{
    [Fact]
    public void PremultiplicationMatchesRoundedByteProductsAndPreservesStridePadding()
    {
        // Every color/alpha pair, plus an odd width and untouched row padding.
        const int width = 259;
        const int stride = width * 4 + 12;
        byte[] pixels = Enumerable.Repeat((byte)0xCD, stride * 256).ToArray();
        for (int alpha = 0; alpha < 256; alpha++)
        for (int x = 0; x < width; x++)
        {
            int i = alpha * stride + x * 4;
            pixels[i] = (byte)(x % 256);
            pixels[i + 1] = (byte)(255 - x % 256);
            pixels[i + 2] = 123;
            pixels[i + 3] = (byte)alpha;
        }
        byte[] expected = (byte[])pixels.Clone();
        for (int alpha = 0; alpha < 256; alpha++)
        for (int x = 0; x < width; x++)
        for (int channel = 0; channel < 3; channel++)
        {
            int i = alpha * stride + x * 4 + channel;
            expected[i] = (byte)((expected[i] * alpha + 127) / 255);
        }
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { PDFiumService.PremultiplyBitmap(handle.AddrOfPinnedObject(), width, 256, stride, TestContext.Current.CancellationToken); }
        finally { handle.Free(); }
        Assert.Equal(expected, pixels);
    }

    [Fact]
    public void PremultiplicationObservesCancellationBeforeTouchingPixels()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            PDFiumService.PremultiplyBitmap(IntPtr.Zero, 259, 256, 1048, cancellation.Token));
    }

    [AvaloniaTheory]
    [InlineData(1.25f)]
    [InlineData(1.5625f)]
    [InlineData(2.5f)]
    public void OpaqueBlackTextProducesLcdPixelsAndOpaqueAlpha(float scale)
    {
        using var scope = UseSkia();
        using var fixture = new RenderingPdf();
        using var service = new PDFiumService();
        using var bitmap = service.RenderPageToWriteableBitmap(fixture.Path, 0, scale);
        using (var frame = bitmap.Lock())
            Assert.Equal(AlphaFormat.Opaque, frame.AlphaFormat);

        byte[] pixels = ReadPixels(bitmap);
        bool hasSubpixelText = false;
        for (int i = 0; i < pixels.Length; i += 4)
            hasSubpixelText |= pixels[i] != pixels[i + 1] || pixels[i + 1] != pixels[i + 2];
        Assert.True(hasSubpixelText, "Black text must retain LCD's RGB subpixel coverage.");

        var raw = service.RenderPageToRawBuffer(fixture.Path, 0, scale);
        for (int i = 0; i < raw.pixels.Length; i += 4)
        {
            Assert.Equal(255, raw.pixels[i + 3]);
            // BGRx's unused byte is deliberately ignored in the direct bitmap.
            for (int channel = 0; channel < 3; channel++)
                Assert.Equal(pixels[i + channel], raw.pixels[i + channel]);
        }
    }

    [AvaloniaFact]
    public void TransparentPagePreservesBackgroundAndPremultipliesColor()
    {
        using var scope = UseSkia();
        using var fixture = new RenderingPdf();
        using var service = new PDFiumService();
        using var bitmap = service.RenderPageToWriteableBitmap(fixture.Path, 1);
        using (var frame = bitmap.Lock())
            Assert.Equal(AlphaFormat.Premul, frame.AlphaFormat);

        byte[] pixels = ReadPixels(bitmap);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Pixel(pixels, bitmap.PixelSize.Width, 5, 5));
        byte[] red = Pixel(pixels, bitmap.PixelSize.Width, 30, 70);
        Assert.InRange(red[3], (byte)126, (byte)129);
        Assert.Equal(red[3], red[2]);
        Assert.Equal(0, red[0]);
        Assert.Equal(0, red[1]);
        // Overlapping half-transparent blue and red retain their accumulated
        // alpha instead of being flattened against white by PDFium.
        byte[] overlap = Pixel(pixels, bitmap.PixelSize.Width, 45, 55);
        Assert.InRange(overlap[3], (byte)189, (byte)193);
        Assert.InRange(overlap[0], (byte)126, (byte)129);
        Assert.InRange(overlap[2], (byte)62, (byte)66);
        byte[] green = Pixel(pixels, bitmap.PixelSize.Width, 120, 70);
        Assert.InRange(green[3], (byte)126, (byte)129);
        Assert.Equal(green[3], green[1]);
        Assert.Equal(0, green[0]);
        Assert.Equal(0, green[2]);
        for (int i = 0; i < pixels.Length; i += 4)
        for (int channel = 0; channel < 3; channel++)
            Assert.True(pixels[i + channel] <= pixels[i + 3], "RGB must be premultiplied by alpha.");
    }

    [AvaloniaFact]
    public void SimpleOpacityUsesOpaqueTargetWhenPdfiumDoesNotNeedBackgroundAlpha()
    {
        using var scope = UseSkia();
        using var fixture = new RenderingPdf(needsBackgroundAlpha: false);
        using var service = new PDFiumService();
        using var bitmap = service.RenderPageToWriteableBitmap(fixture.Path, 1);
        using (var frame = bitmap.Lock())
            Assert.Equal(AlphaFormat.Opaque, frame.AlphaFormat);
        byte[] red = Pixel(ReadPixels(bitmap), bitmap.PixelSize.Width, 30, 70);
        Assert.Equal(255, red[2]);
        Assert.InRange(red[0], (byte)126, (byte)129);
        Assert.Equal(red[0], red[1]);
    }

    [AvaloniaFact]
    public void DirectRawAndBatchRenderingHaveTheSamePixels()
    {
        using var scope = UseSkia();
        using var fixture = new RenderingPdf();
        using var service = new PDFiumService();
        var rawPages = service.RenderPagesToRawBuffers(fixture.Path, [0, 1], 1.25f);
        var batch = service.RenderPagesToWriteableBitmaps(fixture.Path, [0, 1], 1.25f);
        var output = new WriteableBitmap[2];
        try
        {
            service.RenderPagesToWriteableBitmaps(fixture.Path, [0, 1], output, 1.25f);
            for (int page = 0; page < 2; page++)
            {
                using var direct = service.RenderPageToWriteableBitmap(fixture.Path, page, 1.25f);
                var raw = service.RenderPageToRawBuffer(fixture.Path, page, 1.25f);
                Assert.Equal(raw.pixels, rawPages[page].pixels);
                Assert.Equal(raw.pixels, ReadPixels(batch[page]));
                Assert.Equal(raw.pixels, ReadPixels(output[page]));
                byte[] directPixels = ReadPixels(direct);
                // Canonicalize BGRx's unused byte for comparison to BGRA.
                if (page == 0)
                    for (int i = 3; i < directPixels.Length; i += 4) directPixels[i] = 255;
                Assert.Equal(raw.pixels, directPixels);
            }
        }
        finally
        {
            foreach (var bitmap in batch.Values) bitmap.Dispose();
            foreach (var bitmap in output) bitmap?.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task TransparentPageComposesOverWhitePaperWithCorrectColors()
    {
        // Exercise the actual page control and Skia, not just its brush or
        // alpha-format properties. Keep the rest of Headless lightweight.
        using var scope = UseSkia();
        using var fixture = new RenderingPdf();
        using var service = new PDFiumService();
        var page = new PdfPageViewModel(1, 160, 100, 1 / DocumentTabViewModel.LayoutScale);
        page.SetImage(service.RenderPageToWriteableBitmap(fixture.Path, 1), 1);
        try
        {
            var control = new PdfPageImage { Page = page, Width = 160, Height = 100 };
            control.Measure(new Size(160, 100));
            control.Arrange(new Rect(0, 0, 160, 100));
            using var surface = SKSurface.Create(new SKImageInfo(160, 100));
            surface.Canvas.Clear(SKColors.Magenta);
            await Avalonia.Skia.Helpers.DrawingContextHelper.RenderAsync(surface.Canvas, control);
            using var image = surface.Snapshot();
            using var result = SKBitmap.FromImage(image);
            Assert.Equal(SKColors.White, result.GetPixel(5, 5));
            SKColor red = result.GetPixel(30, 70);
            Assert.Equal(255, red.Alpha);
            Assert.Equal(255, red.Red);
            Assert.InRange(red.Green, (byte)126, (byte)129);
            Assert.Equal(red.Green, red.Blue);
            SKColor overlap = result.GetPixel(45, 55);
            Assert.InRange(overlap.Red, (byte)126, (byte)131);
            Assert.InRange(overlap.Green, (byte)62, (byte)66);
            Assert.InRange(overlap.Blue, (byte)189, (byte)195);
            SKColor green = result.GetPixel(120, 70);
            Assert.Equal(255, green.Green);
            Assert.InRange(green.Red, (byte)126, (byte)129);
            Assert.Equal(green.Red, green.Blue);
        }
        finally { page.ClearImage(); }
    }

    private static IDisposable UseSkia()
    {
        var scope = (IDisposable)typeof(AvaloniaLocator).GetMethod("EnterScope")!.Invoke(null, null)!;
        SkiaPlatform.Initialize();
        return scope;
    }

    private static byte[] ReadPixels(WriteableBitmap bitmap)
    {
        using var frame = bitmap.Lock();
        int rowBytes = bitmap.PixelSize.Width * 4;
        var pixels = new byte[rowBytes * bitmap.PixelSize.Height];
        for (int y = 0; y < bitmap.PixelSize.Height; y++)
            Marshal.Copy(frame.Address + y * frame.RowBytes, pixels, y * rowBytes, rowBytes);
        return pixels;
    }

    private static byte[] Pixel(byte[] pixels, int width, int x, int y) =>
        pixels.AsSpan((y * width + x) * 4, 4).ToArray();

    private sealed class RenderingPdf : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pdf-rendering-{Guid.NewGuid():N}.pdf");

        public RenderingPdf(bool needsBackgroundAlpha = true)
        {
            const string opaque = "BT /F1 10 Tf 10 70 Td (Small text: minimum illl 0123456789) Tj ET";
            // Ordinary opacity alone does not require background alpha in
            // PDFium. A Screen blend exercises Chromium's BGRA branch too.
            string transparent = "q /GS1 gs 1 0 0 rg 20 20 40 40 re f 0 0 1 rg 40 40 40 40 re f Q"
                + (needsBackgroundAlpha ? " q /GS2 gs 0 1 0 rg 110 20 20 20 re f Q" : "");
            string[] objects =
            [
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 160 100] /Resources << /Font << /F1 5 0 R >> >> /Contents 6 0 R >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 160 100] /Group << /S /Transparency /CS /DeviceRGB /I true >> /Resources << /ExtGState << /GS1 8 0 R /GS2 9 0 R >> >> /Contents 7 0 R >>",
                "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
                $"<< /Length {opaque.Length} >>\nstream\n{opaque}\nendstream",
                $"<< /Length {transparent.Length} >>\nstream\n{transparent}\nendstream",
                "<< /Type /ExtGState /ca 0.5 /CA 0.5 >>",
                "<< /Type /ExtGState /ca 0.5 /CA 0.5 /BM /Screen >>"
            ];
            using var pdf = new MemoryStream();
            void Write(string value) => pdf.Write(Encoding.ASCII.GetBytes(value));
            Write("%PDF-1.4\n");
            var offsets = new List<long>();
            for (int i = 0; i < objects.Length; i++)
            {
                offsets.Add(pdf.Length);
                Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
            }
            long xref = pdf.Length;
            Write($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
            foreach (long offset in offsets) Write($"{offset:0000000000} 00000 n \n");
            Write($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            File.WriteAllBytes(Path, pdf.ToArray());
        }

        public void Dispose() => File.Delete(Path);
    }
}
