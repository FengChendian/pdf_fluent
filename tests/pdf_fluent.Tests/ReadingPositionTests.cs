using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using pdf_fluent.Controls;
using pdf_fluent.Models;
using pdf_fluent.Services;
using pdf_fluent.ViewModels;
using pdf_fluent.Views;
using Xunit;

namespace pdf_fluent.Tests;

public sealed class ReadingPositionTests
{
    [AvaloniaFact]
    public async Task SwitchingTabsAndHomeReusesBitmapsAndClosingReleasesOnlyThatTab()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pdf_fluent-tests", Guid.NewGuid().ToString("N"));
        using var viewModel = new MainViewModel(
            new PDFiumService(),
            new RecentDocumentsService(Path.Combine(directory, "recent.json")),
            new FavoriteDocumentsService(Path.Combine(directory, "favorites.json")));
        using var firstService = new FakePdfDocumentService(8, 640, 800);
        using var secondService = new FakePdfDocumentService(8, 640, 800);
        var first = new DocumentTabViewModel("first.pdf", firstService);
        var second = new DocumentTabViewModel("second.pdf", secondService);
        await first.LoadAsync();
        await second.LoadAsync();
        firstService.AllowRenders();
        secondService.AllowRenders();
        viewModel.OpenDocuments.Add(first);
        viewModel.OpenDocuments.Add(second);
        viewModel.SelectedDocument = first;
        var window = new MainWindow { DataContext = viewModel };

        try
        {
            async Task WaitForPagesAsync()
            {
                window.UpdateLayout();
                await PdfTestHelpers.WaitUntilAsync(() =>
                    Reader(window).PageItems.GetRealizedContainers()
                        .Select(container => container.DataContext as PdfPageViewModel)
                        .OfType<PdfPageViewModel>()
                        .All(page => page.Image is not null) &&
                    viewModel.SelectedDocument!.PendingRenderCount == 0);
            }

            window.Show();
            await WaitForPagesAsync();
            ScrollToPage(window, first, 2);
            await WaitForPagesAsync();
            var firstBitmaps = first.Pages.Where(page => page.Image is not null)
                .ToDictionary(page => page, page => page.Image!);
            Assert.NotEmpty(firstBitmaps);
            int firstRenderCount = firstService.RenderCallCount;

            viewModel.SelectedDocument = second;
            await WaitForPagesAsync();
            ScrollToPage(window, second, 4);
            await WaitForPagesAsync();
            var secondBitmaps = second.Pages.Where(page => page.Image is not null)
                .ToDictionary(page => page, page => page.Image!);
            Assert.NotEmpty(secondBitmaps);
            int secondRenderCount = secondService.RenderCallCount;

            for (int iteration = 0; iteration < 2; iteration++)
            {
                viewModel.ShowHomeCommand.Execute(null);
                window.UpdateLayout();
                foreach (DocumentTabViewModel document in new[] { first, second })
                {
                    viewModel.SelectedDocument = document;
                    await WaitForPagesAsync();
                }
            }

            Assert.Equal(firstRenderCount, firstService.RenderCallCount);
            Assert.Equal(secondRenderCount, secondService.RenderCallCount);
            Assert.All(firstBitmaps, entry => Assert.Same(entry.Value, entry.Key.Image));
            Assert.All(secondBitmaps, entry => Assert.Same(entry.Value, entry.Key.Image));
            Assert.Equal(PdfTestHelpers.LiveBitmapBytes(first), first.CachedBitmapBytes);
            Assert.Equal(PdfTestHelpers.LiveBitmapBytes(second), second.CachedBitmapBytes);

            await viewModel.CloseDocumentCommand.ExecuteAsync(first);
            window.UpdateLayout();

            Assert.Same(second, viewModel.SelectedDocument);
            Assert.DoesNotContain(first, viewModel.OpenDocuments);
            Assert.Equal(0, first.CachedBitmapBytes);
            Assert.Equal(0, first.CachedPageCount);
            Assert.All(firstBitmaps, entry =>
            {
                Assert.Null(entry.Key.Image);
                Assert.Throws<ObjectDisposedException>(() => entry.Value.Lock());
            });
            Assert.All(secondBitmaps, entry => Assert.Same(entry.Value, entry.Key.Image));
            Assert.Equal(secondRenderCount, secondService.RenderCallCount);
        }
        finally
        {
            window.Close();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task SwitchingTabsAndHomeRestoresAndPersistsEachScrollOffset()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pdf_fluent-tests", Guid.NewGuid().ToString("N"));
        string recentPath = Path.Combine(directory, "recent.json");
        using var viewModel = new MainViewModel(
            new PDFiumService(),
            new RecentDocumentsService(recentPath),
            new FavoriteDocumentsService(Path.Combine(directory, "favorites.json")));
        using var firstService = new FakePdfDocumentService(8);
        using var secondService = new FakePdfDocumentService(8);
        var first = new DocumentTabViewModel("first.pdf", firstService);
        var second = new DocumentTabViewModel("second.pdf", secondService);
        await first.LoadAsync();
        await second.LoadAsync();
        viewModel.OpenDocuments.Add(first);
        viewModel.OpenDocuments.Add(second);
        viewModel.RecentDocuments.Add(new RecentDocumentViewModel(new RecentDocument
        {
            FilePath = first.FilePath,
            PageCount = first.PageCount,
        }));
        viewModel.RecentDocuments.Add(new RecentDocumentViewModel(new RecentDocument
        {
            FilePath = second.FilePath,
            PageCount = second.PageCount,
        }));
        viewModel.SelectedDocument = first;
        var window = new MainWindow { DataContext = viewModel };

        try
        {
            window.Show();
            window.UpdateLayout();
            ScrollToPage(window, first, 2);
            Vector firstOffset = first.ScrollOffset;
            Assert.True(firstOffset.Y > 0);

            viewModel.SelectedDocument = second;
            window.UpdateLayout();
            ScrollToPage(window, second, 4);
            Vector secondOffset = second.ScrollOffset;
            Assert.True(secondOffset.Y > firstOffset.Y);

            viewModel.ShowHomeCommand.Execute(null);
            window.UpdateLayout();
            viewModel.SelectedDocument = first;
            window.UpdateLayout();
            Assert.Equal(firstOffset.Y, Reader(window).DocumentScroller.Offset.Y, precision: 1);

            viewModel.ShowHomeCommand.Execute(null);
            window.UpdateLayout();
            viewModel.SelectedDocument = second;
            window.UpdateLayout();
            Assert.Equal(secondOffset.Y, Reader(window).DocumentScroller.Offset.Y, precision: 1);

            RecentDocument[] stored = new RecentDocumentsService(recentPath).Load()
                .OrderBy(document => document.FilePath)
                .ToArray();
            Assert.Equal(2, stored.Length);
            Assert.Equal(firstOffset.Y, stored[0].ScrollOffsetY, precision: 1);
            Assert.Equal(secondOffset.Y, stored[1].ScrollOffsetY, precision: 1);
        }
        finally
        {
            window.Close();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task OpeningFromRecentDocumentsJumpsToThePersistedOffset()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pdf_fluent-tests", Guid.NewGuid().ToString("N"));
        string recentPath = Path.Combine(directory, "recent.json");
        string filePath = PdfTestHelpers.SamplePdfPath();
        const double savedOffset = 2400;
        new RecentDocumentsService(recentPath).Save([
            new RecentDocument
            {
                FilePath = filePath,
                ScrollOffsetY = savedOffset,
                ScrollLayoutVersion = 1,
                LastOpened = DateTimeOffset.Now,
            },
        ]);
        using var viewModel = new MainViewModel(
            new PDFiumService(),
            new RecentDocumentsService(recentPath),
            new FavoriteDocumentsService(Path.Combine(directory, "favorites.json")));
        var window = new MainWindow { DataContext = viewModel };

        try
        {
            window.Show();
            await viewModel.OpenRecentCommand.ExecuteAsync(viewModel.RecentDocuments.Single());
            window.UpdateLayout();

            DocumentTabViewModel document = Assert.IsType<DocumentTabViewModel>(viewModel.SelectedDocument);
            Assert.Equal(savedOffset, document.ScrollOffset.Y, precision: 1);
            Assert.Equal(savedOffset, Reader(window).DocumentScroller.Offset.Y, precision: 1);
            Assert.True(document.CurrentPage > 1);
        }
        finally
        {
            window.Close();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static PdfDocumentView Reader(Window window) =>
        window.GetVisualDescendants().OfType<PdfDocumentView>().Single();

    private static void ScrollToPage(Window window, DocumentTabViewModel document, int pageIndex)
    {
        PdfDocumentView reader = Reader(window);
        var panel = reader.GetVisualDescendants().OfType<PdfVirtualizingPanel>().Single();
        reader.DocumentScroller.Offset = new Vector(0, panel.Padding.Top + panel.ItemTopAt(pageIndex, document.Zoom));
        window.UpdateLayout();
    }
}
