using System.Runtime.InteropServices;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using pdf_fluent.Services;
using Xunit;
using static pdf_fluent.Tests.PdfTestHelpers;

namespace pdf_fluent.Tests;

public sealed class PdfRenderingPipelineTests
{
    [AvaloniaFact]
    public async Task BackgroundRenderingReusesTheDocumentAndWritesPixelsDirectly()
    {
        string filePath = SamplePdfPath();
        using var service = new PDFiumService();
        service.GetPageSizes(filePath);

        WriteableBitmap first = await Task.Run(() =>
            service.RenderPageToWriteableBitmap(filePath, 0, 0.5f));
        using (first)
        {
            WriteableBitmap second = await Task.Run(() =>
                service.RenderPageToWriteableBitmap(filePath, 1, 0.5f));
            using (second)
            {
                Assert.Equal(1, service.DocumentOpenCount);
                Assert.True(first.PixelSize.Width > 0);
                Assert.True(first.PixelSize.Height > 0);

                byte[] pixels;
                using (var target = first.Lock())
                {
                    pixels = new byte[target.RowBytes * first.PixelSize.Height];
                    Marshal.Copy(target.Address, pixels, 0, pixels.Length);
                }

                Assert.Contains(pixels, value => value < 250);
            }
        }

        service.ReleaseDocument(filePath);
        WriteableBitmap reopened = await Task.Run(() =>
            service.RenderPageToWriteableBitmap(filePath, 0, 0.25f));
        using (reopened)
        {
            Assert.Equal(2, service.DocumentOpenCount);
        }
    }

}
