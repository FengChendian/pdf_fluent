using Avalonia.Headless.XUnit;
using pdf_fluent.Models;
using pdf_fluent.Services;
using pdf_fluent.ViewModels;
using Xunit;
using static pdf_fluent.Tests.PdfTestHelpers;

namespace pdf_fluent.Tests;

public sealed class PdfTextFeatureTests
{
    [AvaloniaFact]
    public async Task BackwardCrossPageSelectionCopiesTextInReadingOrder()
    {
        using var service = new FakePdfDocumentService(3);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        PdfTextPage first = await document.GetTextPageAsync(0);
        PdfTextPage second = await document.GetTextPageAsync(1);

        document.SetSelection(new PdfTextRange(
            new PdfTextPosition(1, 3),
            new PdfTextPosition(0, 2)));

        string expected = first.GetText(2, first.CharacterCount - 2) +
                          Environment.NewLine + Environment.NewLine + second.GetText(0, 3);
        Assert.Equal(expected, await document.GetSelectedTextAsync());
        Assert.Single(document.GetSelectionRects(0));
        Assert.Single(document.GetSelectionRects(1));
    }

    [AvaloniaFact]
    public async Task SearchBuildsContextsAndNavigatesToTheExactRange()
    {
        using var service = new FakePdfDocumentService(3);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();

        document.SearchQuery = "text";
        await WaitUntilAsync(() => !document.IsSearching && document.SearchResults.Count == 3);

        Assert.Equal([1, 2, 3], document.SearchResults.Select(result => result.PageNumber).ToArray());
        Assert.All(document.SearchResults, result => Assert.Equal("PDF text", result.Context));
        document.GoToSearchResult(document.SearchResults[1]);
        Assert.Equal(2, document.CurrentPage);
        Assert.Equal(4, document.Selection!.Value.Start.CharacterIndex);
        Assert.Equal(8, document.Selection.Value.End.CharacterIndex);
    }

    [AvaloniaFact]
    public async Task HighlightUsesMergedCharacterBoxesAndSupportsWordClick()
    {
        using var service = new FakePdfDocumentService(1);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        await document.GetTextPageAsync(0);

        await document.AddHighlightAsync(new PdfTextRange(
            new PdfTextPosition(0, 0),
            new PdfTextPosition(0, 4)));
        await document.AddWordHighlightAsync(0, 1);

        Assert.Equal(2, document.Highlights.Count);
        Assert.All(document.Highlights, highlight => Assert.Single(highlight.Rects));
        Assert.Equal(new PdfRect(20, 750, 60, 738), document.Highlights[0].Rects[0]);
        Assert.Equal(new PdfRect(20, 750, 50, 738), document.Highlights[1].Rects[0]);
        Assert.Null(document.HighlightErrorMessage);
        document.HighlightCornerRadius = 5;
        Assert.Equal(5, document.HighlightCornerRadius);
    }

    [AvaloniaFact]
    public void PdfiumProvidesTextUnicodeAndCropRelativeGeometry()
    {
        using var service = new PDFiumService();
        string filePath = SamplePdfPath();
        (int pageIndex, PdfTextPage page) = FirstVisibleTextPage(service, filePath);

        Assert.True(pageIndex >= 0);
        Assert.False(string.IsNullOrWhiteSpace(page.Text));
        PdfTextCharacter character = page.Characters.First(item =>
            item.HasGeometry && item.Bounds.Intersects(page.CropBox));
        PdfTextHit hit = Assert.NotNull(page.HitTest(
            character.LooseBounds.CenterX,
            character.LooseBounds.CenterY));
        Assert.Equal(character.Index, hit.CharacterIndex);
        Assert.NotEmpty(page.GetMergedRects(character.Index, 1));
    }

    [AvaloniaFact]
    public async Task HighlightPersistsWithoutChangingHighlightsOnUnloadedPages()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pdf_fluent_tests");
        Directory.CreateDirectory(directory);
        string filePath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".pdf");
        File.Copy(SamplePdfPath(), filePath);
        try
        {
            List<PdfHighlight> originalHighlights;
            int pageIndex;
            using (var service = new PDFiumService())
            {
                originalHighlights = service.LoadHighlights(filePath);
                using var document = new DocumentTabViewModel(filePath, service);
                await document.LoadAsync();
                Assert.Empty(document.Highlights);
                HashSet<int> pagesWithOriginalHighlights = originalHighlights
                    .Select(highlight => highlight.PageIndex)
                    .ToHashSet();
                (_, PdfTextPage text) = FirstVisibleTextPage(
                    service, filePath, pagesWithOriginalHighlights);
                pageIndex = text.PageIndex;
                int characterIndex = text.Characters.First(character =>
                    character.HasGeometry && character.Bounds.Intersects(text.CropBox)).Index;
                await document.AddHighlightAsync(new PdfTextRange(
                    new PdfTextPosition(pageIndex, characterIndex),
                    new PdfTextPosition(pageIndex, characterIndex + 1)));
                Assert.Null(document.HighlightErrorMessage);
                Assert.True(document.HasUnsavedHighlights);
                Assert.Single(document.Highlights);
                Assert.True(await document.SaveHighlightsAsync());
                Assert.False(document.HasUnsavedHighlights);
            }

            using var reader = new PDFiumService();
            List<PdfHighlight> saved = reader.LoadHighlights(filePath);
            Assert.Equal(originalHighlights.Count + 1, saved.Count);
            Assert.Contains(saved, highlight => highlight.PageIndex == pageIndex && highlight.Rects.Count > 0);
            foreach (PdfHighlight original in originalHighlights)
            {
                Assert.Contains(saved, highlight =>
                    highlight.PageIndex == original.PageIndex &&
                    highlight.Rects.SequenceEqual(original.Rects));
            }
        }
        finally
        {
            if (File.Exists(filePath)) File.Delete(filePath);
        }
    }

    private static (int PageIndex, PdfTextPage Text) FirstVisibleTextPage(
        PDFiumService service,
        string filePath,
        IReadOnlySet<int>? excludedPages = null)
    {
        for (int pageIndex = 0; pageIndex < service.GetPageSizes(filePath).Length; pageIndex++)
        {
            if (excludedPages?.Contains(pageIndex) == true) continue;
            PdfTextPage text = service.GetPageText(filePath, pageIndex);
            if (text.Characters.Any(character =>
                    character.HasGeometry && character.Bounds.Intersects(text.CropBox)))
                return (pageIndex, text);
        }
        throw new InvalidOperationException("测试样本没有可见文本。");
    }
}
