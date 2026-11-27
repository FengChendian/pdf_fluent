using System.Text;
using Avalonia;
using Avalonia.Headless.XUnit;
using PDFiumCore;
using pdf_fluent.Models;
using pdf_fluent.Services;
using pdf_fluent.ViewModels;
using Xunit;

namespace pdf_fluent.Tests;

public sealed class PdfLinkTests
{
    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PdfiumExtractsAnnotationsNamedTargetsAndPlainUrlsWithCropAndRotation(int rotation)
    {
        using var fixture = new LinkPdfFixture(rotation, rotation);
        using var service = new PDFiumService();
        PdfLinkPage text = service.GetPageLinks(fixture.Path, 0);
        Assert.Equal(rotation, text.Rotation);
        Assert.Equal(new PdfRect(10, 780, 590, 20), text.CropBox);
        PdfLink Hit(double x, double y) => Assert.IsType<PdfLink>(text.HitTestLink(
            PdfLinkCoordinates.ToPage(new Point(x, y), text.CropBox, rotation)));

        PdfDestination direct = Assert.IsType<PdfDestination>(Hit(420, 740).Target!.Destination);
        Assert.Equal(1, direct.PageIndex);
        Assert.Equal(new PdfRect(20, 950, 680, 30), direct.PageBox);
        Assert.Equal(rotation, direct.Rotation);
        Assert.Equal(70, direct.X);
        Assert.Equal(600, direct.Y);
        Assert.Equal(1.5, direct.Zoom);
        Assert.Equal(direct, Hit(420, 680).Target!.Destination);
        PdfDestination goTo = Assert.IsType<PdfDestination>(Hit(420, 620).Target!.Destination);
        Assert.Equal(2, goTo.PageIndex);
        Assert.Equal(PdfDestinationView.FitH, goTo.View);
        Assert.Equal(700, goTo.Y);
        PdfDestination unspecified = Assert.IsType<PdfDestination>(Hit(420, 360).Target!.Destination);
        Assert.Null(unspecified.X);
        Assert.Null(unspecified.Y);
        Assert.Null(unspecified.Zoom);
        PdfDestination fit = Assert.IsType<PdfDestination>(Hit(420, 300).Target!.Destination);
        Assert.Equal(PdfDestinationView.FitR, fit.View);
        Assert.Equal(new PdfRect(40, 800, 300, 200), fit.FitRect);
        Assert.Equal("https://actual.example/target", Hit(60, 690).Target!.Uri!.AbsoluteUri);
        Assert.Equal("https://plain.example/path", Hit(60, 730).Target!.Uri!.AbsoluteUri);
        Assert.Equal("https://multiline.example/", Hit(60, 614).Target!.Uri!.AbsoluteUri);
        Assert.Equal("https://multiline.example/", Hit(60, 584).Target!.Uri!.AbsoluteUri);
        Assert.Null(text.HitTestLink(PdfLinkCoordinates.ToPage(new Point(60, 600), text.CropBox, rotation)));
        Assert.Null(text.HitTestLink(PdfLinkCoordinates.ToPage(new Point(420, 540), text.CropBox, rotation)));
        Assert.Null(text.HitTestLink(PdfLinkCoordinates.ToPage(new Point(420, 480), text.CropBox, rotation)));
        Assert.Null(text.HitTestLink(PdfLinkCoordinates.ToPage(new Point(420, 420), text.CropBox, rotation)));

        // Compare the managed mapping against PDFium itself, not its inverse.
        var native = fpdfview.FPDF_LoadDocument(fixture.Path, null);
        var page = fpdfview.FPDF_LoadPage(native, 0);
        try
        {
            int x = 0, y = 0;
            int width = rotation % 2 == 0 ? 580 : 760;
            int height = rotation % 2 == 0 ? 760 : 580;
            Assert.NotEqual(0, fpdfview.FPDF_PageToDevice(page, 0, 0, width, height, 0, 420, 740, ref x, ref y));
            Assert.Equal(new Point(x, y), PdfLinkCoordinates.ToPage(new Point(420, 740), text.CropBox, rotation));
        }
        finally { fpdfview.FPDF_ClosePage(page); fpdfview.FPDF_CloseDocument(native); }
        service.ReleaseDocument(fixture.Path);
        Assert.NotNull(Hit(420, 740)); // no retained native pointers
    }

    [Theory]
    [InlineData("https://example.com/a", true)]
    [InlineData("http://example.com/", true)]
    [InlineData("mailto:reader@example.com", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/Windows/notepad.exe", false)]
    [InlineData("ms-settings:display", false)]
    [InlineData("https://example.com/\nattack", false)]
    [InlineData("relative.pdf#page=2", false)]
    public void OnlySupportedExternalProtocolsAreActionable(string url, bool supported) =>
        Assert.Equal(supported, PdfLinkTarget.FromUri(url) is not null);

    [Fact]
    public void TopmostAnnotationWinsAndUnsupportedAnnotationBlocksAutomaticUrl()
    {
        PdfLinkRegion region = PdfLinkRegion.FromRect(new PdfRect(0, 100, 100, 0));
        var automatic = new PdfLink([region], PdfLinkTarget.FromUri("https://automatic.example"));
        var explicitLink = new PdfLink([region], PdfLinkTarget.FromUri("https://explicit.example"));
        var text = new PdfLinkPage(0, new PdfRect(0, 100, 100, 0), [automatic, explicitLink]);
        Assert.Same(explicitLink, text.HitTestLink(new Point(30, 30)));
        text = new PdfLinkPage(0, new PdfRect(0, 100, 100, 0), [automatic, new PdfLink([region], null)]);
        Assert.Null(text.HitTestLink(new Point(30, 30)));
    }

    [Fact]
    public void SlantedQuadDoesNotTurnItsBoundingBoxCornersIntoLinks()
    {
        var region = new PdfLinkRegion(new Point(0, 80), new Point(100, 100), new Point(100, 80), new Point(0, 60));
        Assert.True(region.Contains(new Point(50, 80)));
        Assert.False(region.Contains(new Point(2, 98)));
    }

    [AvaloniaFact]
    public async Task SavingHighlightsPreservesExistingLinksAndManagedTargetsRemainUsable()
    {
        using var fixture = new LinkPdfFixture();
        using var service = new PDFiumService();
        using var document = new DocumentTabViewModel(fixture.Path, service);
        await document.LoadAsync();
        PdfLinkPage before = await document.GetLinkPageAsync(0);
        await document.AddWordHighlightAsync(0, 1);
        Assert.True(await document.SaveHighlightsAsync(), document.HighlightErrorMessage);
        PdfLinkPage after = service.GetPageLinks(fixture.Path, 0);
        Assert.Equal(before.Links.Count, after.Links.Count);
        Assert.Equal(before.Links.Select(link => link.Target), after.Links.Select(link => link.Target));
        Assert.Equal("https://actual.example/target", after.HitTestLink(new Point(50, 90))!.Target!.Uri!.AbsoluteUri);
        Assert.NotEmpty(service.LoadHighlights(fixture.Path, 0));
        Assert.NotNull(before.HitTestLink(new Point(410, 40))!.Target!.Destination);
    }
}

internal sealed class LinkPdfFixture : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pdf-links-{Guid.NewGuid():N}.pdf");

    public LinkPdfFixture(int sourceRotation = 0, int targetRotation = 0)
    {
        const string stream = "BT /F1 12 Tf 30 730 Td (https://plain.example/path) Tj 0 -40 Td (https://visible.example) Tj ET\n";
        string[] annotations =
        [
            "/Rect [380 720 500 760] /Dest [4 0 R /XYZ 70 600 1.5]",
            "/Rect [380 660 500 700] /Dest (named)",
            "/Rect [380 600 500 640] /A << /S /GoTo /D [5 0 R /FitH 700] >>",
            "/Rect [25 680 350 708] /A << /S /URI /URI (https://actual.example/target) >>",
            "/Rect [25 570 200 630] /QuadPoints [30 620 120 620 30 608 120 608 30 590 130 590 30 578 130 578] /A << /S /URI /URI (https://multiline.example/) >>",
            "/Rect [380 520 500 560] /A << /S /Launch /F (unsafe.exe) >>",
            "/Rect [380 460 500 500] /A << /S /URI /URI (javascript:alert\\(1\\)) >>",
            "/Rect [380 400 500 440] /Dest [999 0 R /Fit]",
            "/Rect [380 340 500 380] /Dest [4 0 R /XYZ null null 0]",
            "/Rect [380 280 500 320] /Dest [4 0 R /FitR 40 200 300 800]",
        ];
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Names << /Dests << /Names [(named) [4 0 R /XYZ 70 600 1.5]] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /CropBox [10 20 590 780] /Rotate {sourceRotation * 90} /Resources << /Font << /F1 6 0 R >> >> /Contents 7 0 R /Annots [{string.Join(' ', Enumerable.Range(8, annotations.Length).Select(i => $"{i} 0 R"))}] >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 700 1000] /CropBox [20 30 680 950] /Rotate {targetRotation * 90} >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream",
        };
        objects.AddRange(annotations.Select(value => $"<< /Type /Annot /Subtype /Link /Border [0 0 0] {value} >>"));
        using var pdf = new MemoryStream();
        var offsets = new List<long>();
        void Write(string value) => pdf.Write(Encoding.ASCII.GetBytes(value));
        Write("%PDF-1.7\n");
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Position);
            Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        long xref = pdf.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (long offset in offsets) Write($"{offset:0000000000} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(Path, pdf.ToArray());
    }

    public void Dispose() => File.Delete(Path);
}
