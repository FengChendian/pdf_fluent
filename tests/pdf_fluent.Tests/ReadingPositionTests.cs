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
