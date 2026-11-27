using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using pdf_fluent.Controls;
using pdf_fluent.Models;
using pdf_fluent.Services;
using pdf_fluent.ViewModels;
using pdf_fluent.Views;
using Xunit;
using static pdf_fluent.Tests.PdfTestHelpers;

namespace pdf_fluent.Tests;

public sealed class PdfNativePageReuseTests
{
    [AvaloniaFact]
    public void TextAndLinksReuseOneNativeTextPageWithoutScanningLinksDuringTextExtraction()
    {
        using var fixture = new LinkPdfFixture();
        using var service = new PDFiumService();
        PdfTextPage text = service.GetPageText(fixture.Path, 0);
        Assert.Contains("https://plain.example/path", text.Text);
        Assert.Equal(0, service.LinkPageLoadCount);
        Assert.Equal(1, service.NativeTextPageLoadCount);
        PdfLinkPage links = service.GetPageLinks(fixture.Path, 0);
        Assert.NotEmpty(links.Links);
        Assert.Equal(1, service.LinkPageLoadCount);
        Assert.Equal(1, service.NativeTextPageLoadCount);
        Assert.Same(links, service.GetPageLinks(fixture.Path, 0));
        Assert.Equal(text.Text, service.GetPageText(fixture.Path, 0).Text);
        Assert.Equal(1, service.LinkPageLoadCount);
        Assert.Equal(1, service.NativeTextPageLoadCount);
    }

    [AvaloniaFact]
    public void NativeCacheIsBoundedAndEvictsLeastRecentlyUsedPages()
    {
        using var service = new PDFiumService();
        string path = SamplePdfPath();
        PdfLinkPage first = service.GetPageLinks(path, 0);
        PdfLinkPage second = service.GetPageLinks(path, 1);
        for (int page = 2; page < PDFiumService.MaxNativeTextPages; page++) service.GetPageLinks(path, page);
        Assert.Equal(PDFiumService.MaxNativeTextPages, service.CachedNativeTextPageCount);
        Assert.Same(first, service.GetPageLinks(path, 0));
        service.GetPageLinks(path, PDFiumService.MaxNativeTextPages);
        Assert.Same(first, service.GetPageLinks(path, 0));
        Assert.NotSame(second, service.GetPageLinks(path, 1));
        Assert.Equal(PDFiumService.MaxNativeTextPages, service.CachedNativeTextPageCount);
        Assert.Equal(PDFiumService.MaxNativeTextPages + 2, service.NativeTextPageLoadCount);
        service.ReleaseDocument(path);
        Assert.Equal(0, service.CachedNativeTextPageCount);
    }

    [AvaloniaFact]
    public void ReleasingOneDocumentKeepsOtherDocumentsNativePagesAlive()
    {
        using var first = new LinkPdfFixture();
        using var second = new LinkPdfFixture();
        using var service = new PDFiumService();
        PdfLinkPage saved = service.GetPageLinks(first.Path, 0);
        PdfLinkPage retained = service.GetPageLinks(second.Path, 0);
        service.ReleaseDocument(first.Path);
        Assert.Equal(1, service.CachedNativeTextPageCount);
        Assert.Same(retained, service.GetPageLinks(second.Path, 0));
        Assert.NotNull(saved.HitTestLink(new Point(410, 40)));
        Assert.NotSame(saved, service.GetPageLinks(first.Path, 0));
        service.ReleaseAllTextPages();
        Assert.Equal(0, service.CachedNativeTextPageCount);
    }

    [AvaloniaFact]
    public async Task SearchingAnUnopenedDocumentDoesNotLoadLinkMetadata()
    {
        using var fixture = new LinkPdfFixture();
        using var service = new PDFiumService();
        using var document = new DocumentTabViewModel(fixture.Path, service);
        await document.LoadAsync();
        document.SearchQuery = "https";
        await WaitUntilAsync(() => !document.IsSearching && document.SearchResults.Count == 2);
        Assert.Equal(0, service.LinkPageLoadCount);
        Assert.All(document.Pages, page => Assert.Null(page.Links));
    }

    [AvaloniaFact]
    public void CanceledRequestsDoNotCreateNativePagesAndDisposalClosesTheCache()
    {
        using var fixture = new LinkPdfFixture();
        using var service = new PDFiumService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => service.GetPageLinks(fixture.Path, 0, cancellation.Token));
        Assert.Equal(0, service.CachedNativeTextPageCount);
        service.GetPageLinks(fixture.Path, 0);
        service.Dispose();
        Assert.Equal(0, service.CachedNativeTextPageCount);
        Assert.Throws<ObjectDisposedException>(() => service.GetPageLinks(fixture.Path, 0));
    }

    [AvaloniaFact]
    public async Task LinksCanHoverAndOpenWhileManagedTextExtractionIsBlocked()
    {
        using var service = new FakePdfDocumentService(3);
        service.BlockTextLoads();
        service.LinkPages[0] = new PdfLinkPage(0, new PdfRect(0, 800, 600, 0),
        [new PdfLink([PdfLinkRegion.FromRect(new PdfRect(20, 751, 100, 737))], PdfLinkTarget.FromUri("https://ready.example"))]);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        var view = new PdfDocumentView { DataContext = document };
        var launched = new List<Uri>();
        view.LinkLauncher = uri => { launched.Add(uri); return Task.FromResult(true); };
        var window = new Window { Width = 1000, Height = 750, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            await document.GetLinkPageAsync(0);
            await service.TextLoadStarted.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Null(document.Pages[0].Text);
            PdfPageImage image = view.PageItems.GetRealizedContainers()
                .SelectMany(container => container.GetVisualDescendants().OfType<PdfPageImage>()).First();
            Rect pointBounds = image.Geometry.FromLogical(new Rect(25 * 1.25, (800 - 744) * 1.25, 1, 1));
            Point point = image.TranslatePoint(pointBounds.TopLeft, window)!.Value;
            window.MouseMove(point);
            Assert.True(view.IsLinkCursor);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Assert.Equal("https://ready.example/", Assert.Single(launched).AbsoluteUri);
            Assert.Null(document.Pages[0].Text);
        }
        finally { service.AllowTextLoads(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task LeavingAndReturningWhileLinksLoadCancelsAndRetriesTheRequest()
    {
        using var service = new FakePdfDocumentService(3);
        service.BlockLinkLoads();
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        PdfPageViewModel page = document.Pages[0];
        document.EnsurePageRendered(page);
        await service.LinkLoadStarted.WaitAsync(TimeSpan.FromSeconds(3));
        document.ReleasePage(page);
        await WaitUntilAsync(() => service.LinkCancellationCount > 0);
        document.EnsurePageRendered(page);
        service.AllowLinkLoads();
        PdfLinkPage loaded = await document.GetLinkPageAsync(0);
        Assert.Same(loaded, page.Links);
        Assert.True(service.RequestedLinkPages.Length >= 2);
    }

    [AvaloniaFact]
    public async Task LinkFailureDoesNotDisableExistingTextFeatures()
    {
        using var service = new FakePdfDocumentService(1) { LinkLoadError = new InvalidOperationException("links failed") };
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        document.EnsurePageRendered(document.Pages[0]);
        await WaitUntilAsync(() => document.Pages[0].Text is not null);
        Assert.Equal("PDF text", document.Pages[0].Text!.Text);
        Assert.Null(document.Pages[0].Links);
    }
}
