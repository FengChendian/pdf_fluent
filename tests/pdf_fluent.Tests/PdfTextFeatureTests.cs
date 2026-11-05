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

    [AvaloniaFact]
    public void PageTextUsesIntersectionOfMediaAndCropBoxes()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pdf_fluent_tests");
        Directory.CreateDirectory(directory);
        string filePath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            var objects = new[]
            {
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] /CropBox [0 0 400 600] >>",
            };
            using var pdf = new MemoryStream();
            var offsets = new List<int>();
            void Write(string value)
            {
                byte[] bytes = System.Text.Encoding.ASCII.GetBytes(value);
                pdf.Write(bytes, 0, bytes.Length);
            }

            Write("%PDF-1.6\n");
            for (int index = 0; index < objects.Length; index++)
            {
                offsets.Add((int)pdf.Length);
                Write($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
            }
            int xref = (int)pdf.Length;
            Write($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
            foreach (int offset in offsets) Write($"{offset:0000000000} 00000 n \n");
            Write($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            File.WriteAllBytes(filePath, pdf.ToArray());

            using var service = new PDFiumService();
            PdfTextPage page = service.GetPageText(filePath, 0);

            Assert.Equal(new PdfRect(0, 300, 200, 0), page.CropBox);
            Assert.Equal((0d, 0d, 200d, 300d), Assert.Single(service.GetAllPageBBoxes(filePath)));
        }
        finally
        {
            if (File.Exists(filePath)) File.Delete(filePath);
        }
    }

    [AvaloniaFact]
    public void HitTestKeepsShortLastLineOutOfTheRightColumn()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pdf_fluent_tests");
        Directory.CreateDirectory(directory);
        string filePath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            var content = new System.Text.StringBuilder();
            content.Append("BT\n/F1 11 Tf\n");
            string[] leftLines = ["Short", "Left column body text", "Left column body text",
                "Left column body text", "Left column body text"];
            for (int row = 0; row < leftLines.Length; row++)
            {
                int y = 700 - row * 20;
                content.Append($"1 0 0 1 50 {y} Tm ({leftLines[row]}) Tj\n");
                content.Append($"1 0 0 1 330 {y} Tm (Right column body) Tj\n");
            }
            content.Append("ET\n");
            string stream = content.ToString();
            byte[] streamBytes = System.Text.Encoding.ASCII.GetBytes(stream);
            WritePdf(filePath,
            [
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
                "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
                $"<< /Length {streamBytes.Length} >>\nstream\n{stream}endstream",
            ]);

            using var service = new PDFiumService();
            PdfTextPage page = service.GetPageText(filePath, 0);
            PdfTextCharacter[] visible = page.Characters
                .Where(character => character.HasGeometry && character.Bounds.Intersects(page.CropBox))
                .ToArray();
            PdfTextCharacter[] shortLine = visible
                .Where(character => character.Bounds.CenterY is > 690 and < 705 &&
                                    character.Bounds.CenterX < 300)
                .ToArray();
            PdfTextCharacter[] longLine = visible
                .Where(character => character.Bounds.CenterY is > 670 and < 685 &&
                                    character.Bounds.CenterX < 300)
                .ToArray();
            PdfTextCharacter[] rightLine = visible
                .Where(character => character.Bounds.CenterY is > 690 and < 705 &&
                                    character.Bounds.CenterX > 300)
                .ToArray();
            Assert.NotEmpty(shortLine);
            Assert.NotEmpty(longLine);
            Assert.NotEmpty(rightLine);

            double shortLineEnd = shortLine.Max(character => character.Bounds.R);
            double longLineEnd = longLine.Max(character => character.Bounds.R);
            double rightColumnStart = rightLine.Min(character => character.Bounds.L);
            double lineY = shortLine.Average(character => character.Bounds.CenterY);

            // The short line's own gap starts at shortLineEnd, while the
            // surrounding body lines only leave longLineEnd..rightColumnStart
            // empty. A pointer between the two midpoints used to snap to the
            // right column; it must stay in the left column.
            double shortLineMidline = (shortLineEnd + rightColumnStart) / 2;
            double gutterMidline = (longLineEnd + rightColumnStart) / 2;
            Assert.True(gutterMidline > shortLineMidline + 10);
            double probeX = (shortLineMidline + gutterMidline) / 2;

            PdfTextHit left = Assert.NotNull(page.HitTest(probeX, lineY, nearestOnLine: true));
            Assert.False(left.IsOnText);
            Assert.True(page.Characters[left.CharacterIndex].Bounds.CenterX < 300);
            Assert.True(page.Characters[left.CharacterIndex].Bounds.R <= shortLineEnd + 0.5);

            PdfTextHit right = Assert.NotNull(page.HitTest(gutterMidline + 10, lineY, nearestOnLine: true));
            Assert.True(page.Characters[right.CharacterIndex].Bounds.CenterX > 300);
        }
        finally
        {
            if (File.Exists(filePath)) File.Delete(filePath);
        }
    }

    private static void WritePdf(string filePath, IReadOnlyList<string> objects)
    {
        using var pdf = new MemoryStream();
        var offsets = new List<int>();
        void Write(string value)
        {
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(value);
            pdf.Write(bytes, 0, bytes.Length);
        }

        Write("%PDF-1.6\n");
        for (int index = 0; index < objects.Count; index++)
        {
            offsets.Add((int)pdf.Length);
            Write($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = (int)pdf.Length;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) Write($"{offset:0000000000} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(filePath, pdf.ToArray());
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
