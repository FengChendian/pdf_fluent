using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using pdf_fluent.Controls;
using pdf_fluent.Models;
using pdf_fluent.Services;
using pdf_fluent.ViewModels;
using Xunit;

namespace pdf_fluent.Tests;

public sealed class PdfHighlightEditingTests
{
    [AvaloniaFact]
    public async Task OverlayGeometryUsesCommittedZoomBeforeDuringAndAfterVisualZoom()
    {
        using var service = new FakePdfDocumentService(1);
        service.AllowRenders();
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        PdfPageViewModel page = document.Pages[0];
        document.EnsurePageRendered(page);
        await document.GetTextPageAsync(0);
        await document.AddHighlightAsync(new PdfTextRange(
            new PdfTextPosition(0, 0),
            new PdfTextPosition(0, 1)));
        var overlay = new PdfPageOverlay { Page = page, Document = document };
        PdfRect highlight = document.Highlights[0].Rects[0];
        Assert.Equal(1.5, document.HighlightCornerRadius);

        document.Zoom = 1.75;
        document.SetVisualZoom(0.9);
        Rect during = overlay.GetLayoutBounds(highlight);
        document.SetVisualZoom(null);
        Rect after = overlay.GetLayoutBounds(highlight);

        double scale = DocumentTabViewModel.LayoutScale * 1.75;
        Assert.Equal(new Rect(20 * scale, 50 * scale, 10 * scale, 12 * scale), during);
        Assert.Equal(during, after);

        document.SetSelection(new PdfTextRange(
            new PdfTextPosition(0, 0),
            new PdfTextPosition(0, 1)));
        PdfRect selection = Assert.Single(document.GetSelectionRects(0));
        Assert.Equal(during, overlay.GetLayoutBounds(selection));
        PdfTextHit hit = Assert.NotNull(document.HitTestText(
            0,
            new Point(during.X + during.Width / 2, during.Y + during.Height / 2)));
        Assert.Equal(0, hit.CharacterIndex);
    }

    [AvaloniaFact]
    public async Task HighlightsLoadOnlyForVisiblePagesAndAreCached()
    {
        using var service = new FakePdfDocumentService(3);
        service.InitialHighlights.Add(new PdfHighlight(
            0, [new PdfRect(20, 750, 50, 738)], Avalonia.Media.Colors.Yellow));
        service.InitialHighlights.Add(new PdfHighlight(
            1, [new PdfRect(20, 750, 50, 738)], Avalonia.Media.Colors.Yellow));
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();

        Assert.Empty(document.Highlights);
        Assert.Empty(service.LoadedHighlightPages);

        document.EnsurePageRendered(document.Pages[0]);
        await PdfTestHelpers.WaitUntilAsync(() => document.Highlights.Count == 1);
        Assert.Equal([0], service.LoadedHighlightPages);

        document.EnsurePageRendered(document.Pages[0]);
        Assert.Equal([0], service.LoadedHighlightPages);

        document.SyncRealizedPages([document.Pages[1]], [document.Pages[1]]);
        await PdfTestHelpers.WaitUntilAsync(() => document.Highlights.Count == 2);
        Assert.Equal([0, 1], service.LoadedHighlightPages);
        Assert.DoesNotContain(document.Highlights, highlight => highlight.PageIndex == 2);
    }

    [AvaloniaFact]
    public async Task NewAndExistingHighlightChangesDoNotWriteUntilSaved()
    {
        using var service = new FakePdfDocumentService(1);
        var existing = new PdfHighlight(
            0,
            [new PdfRect(20, 750, 50, 738)],
            Avalonia.Media.Colors.Yellow);
        service.InitialHighlights.Add(existing);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        document.EnsurePageRendered(document.Pages[0]);
        await PdfTestHelpers.WaitUntilAsync(() => document.Highlights.Count == 1);
        Assert.False(document.HasUnsavedHighlights);
        Assert.Single(document.Highlights);

        await document.AddWordHighlightAsync(0, 1);
        Assert.True(document.HasUnsavedHighlights);
        Assert.Equal("unused *", document.DisplayFileName);
        Assert.Equal(0, service.SaveCallCount);
        PdfHighlight added = document.Highlights.Last();

        Assert.True(document.RemoveHighlight(added));
        Assert.Equal(0, service.SaveCallCount);
        Assert.False(document.HasUnsavedHighlights);
        Assert.True(document.RemoveHighlight(document.Highlights[0]));
        Assert.Empty(document.Highlights);
        Assert.True(document.HasUnsavedHighlights);
        Assert.Equal(0, service.SaveCallCount);
    }

    [AvaloniaFact]
    public async Task RemovingNewHighlightRestoresCleanStateAndClosesWithoutConfirmation()
    {
        using var service = new FakePdfDocumentService(1);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        await document.AddWordHighlightAsync(0, 1);
        PdfHighlight added = Assert.Single(document.Highlights);
        Assert.True(document.HasUnsavedHighlights);

        Assert.True(document.RemoveHighlight(added));
        Assert.False(document.HasUnsavedHighlights);
        Assert.Equal("unused", document.DisplayFileName);

        using var viewModel = new MainViewModel();
        viewModel.OpenDocuments.Add(document);
        viewModel.SelectedDocument = document;
        bool confirmationCalled = false;
        viewModel.DocumentCloseConfirmation = _ =>
        {
            confirmationCalled = true;
            return Task.FromResult(DocumentCloseDecision.Cancel);
        };
        await viewModel.CloseDocumentCommand.ExecuteAsync(document);

        Assert.False(confirmationCalled);
        Assert.DoesNotContain(document, viewModel.OpenDocuments);
        Assert.Equal(0, service.SaveCallCount);
    }

    [AvaloniaFact]
    public async Task ClosingDirtyDocumentSupportsCancelFailedSaveAndDiscard()
    {
        using var viewModel = new MainViewModel();
        using var service = new FakePdfDocumentService(1);
        using var document = new DocumentTabViewModel("missing.pdf", service);
        await document.LoadAsync();
        await document.AddWordHighlightAsync(0, 1);
        viewModel.OpenDocuments.Add(document);
        viewModel.SelectedDocument = document;

        viewModel.DocumentCloseConfirmation = _ => Task.FromResult(DocumentCloseDecision.Cancel);
        await viewModel.CloseDocumentCommand.ExecuteAsync(document);
        Assert.Contains(document, viewModel.OpenDocuments);

        viewModel.DocumentCloseConfirmation = _ => Task.FromResult(DocumentCloseDecision.Save);
        await viewModel.CloseDocumentCommand.ExecuteAsync(document);
        Assert.Contains(document, viewModel.OpenDocuments);
        Assert.Contains("保存失败", viewModel.StatusMessage);

        viewModel.DocumentCloseConfirmation = _ => Task.FromResult(DocumentCloseDecision.Discard);
        await viewModel.CloseDocumentCommand.ExecuteAsync(document);
        Assert.DoesNotContain(document, viewModel.OpenDocuments);
        Assert.Equal(0, service.SaveCallCount);
    }

    [AvaloniaFact]
    public async Task WindowCloseCanBeCancelledOrDiscardHighlightChanges()
    {
        using var viewModel = new MainViewModel();
        using var service = new FakePdfDocumentService(1);
        using var document = new DocumentTabViewModel("missing.pdf", service);
        await document.LoadAsync();
        await document.AddWordHighlightAsync(0, 1);
        viewModel.OpenDocuments.Add(document);
        viewModel.SelectedDocument = document;
        var window = new Views.MainWindow { DataContext = viewModel };
        viewModel.DocumentCloseConfirmation = _ => Task.FromResult(DocumentCloseDecision.Cancel);
        try
        {
            window.Show();
            window.Close();
            await Task.Delay(60);
            Assert.True(window.IsVisible);
            Assert.Contains(document, viewModel.OpenDocuments);

            viewModel.DocumentCloseConfirmation = _ => Task.FromResult(DocumentCloseDecision.Discard);
            window.Close();
            await Task.Delay(60);
            Assert.False(window.IsVisible);
        }
        finally
        {
            if (window.IsVisible)
            {
                viewModel.DocumentCloseConfirmation = _ => Task.FromResult(DocumentCloseDecision.Discard);
                window.Close();
            }
        }
    }
}
