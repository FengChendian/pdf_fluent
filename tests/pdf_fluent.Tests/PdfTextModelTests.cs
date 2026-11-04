using pdf_fluent.Models;
using pdf_fluent.Services;
using Xunit;

namespace pdf_fluent.Tests;

public sealed class PdfTextModelTests
{
    [Fact]
    public void HitTestUsesSpatialLinesInsteadOfReadingOrder()
    {
        var page = new PdfTextPage(0, new PdfRect(0, 200, 200, 0),
        [
            Character(0, 'B', 0, 10),
            Character(1, 'A', 0, 110),
            Character(2, 'B', 10, 110),
        ]);

        PdfTextHit hit = Assert.NotNull(page.HitTest(4, 105));
        Assert.Equal(1, hit.CharacterIndex);
        Assert.Equal(1, hit.CaretIndex);
        Assert.True(hit.IsOnText);
        Assert.Null(page.HitTest(80, 105));

        PdfTextHit nearest = Assert.NotNull(page.HitTest(80, 105, nearestOnLine: true));
        Assert.Equal(2, nearest.CharacterIndex);
        Assert.Equal(3, nearest.CaretIndex);
    }

    [Fact]
    public void UnicodeOffsetsMapBackToPdfiumCharacterIndices()
    {
        PdfTextPage page = CreatePage("A😀B");

        Assert.Equal("A😀B", page.Text);
        Assert.Equal("😀B", page.GetText(1, 2));
        PdfSearchResult result = Assert.Single(PdfTextSearch.SearchPage(page, "😀b"));
        Assert.Equal(1, result.CharacterIndex);
        Assert.Equal(2, result.CharacterCount);
        Assert.Equal("A😀B", result.Context);
        Assert.Single(page.GetMergedRects(0, 3));
    }

    [Fact]
    public void GeometryMergesLinesAndClipsCharactersToCropBox()
    {
        var page = new PdfTextPage(0, new PdfRect(5, 120, 35, 0),
        [
            Character(0, 'A', 0, 110),
            Character(1, 'B', 10, 110),
            Character(2, 'C', 30, 110),
            Character(3, 'D', 0, 90),
            Character(4, 'E', 0, 50),
        ]);

        IReadOnlyList<PdfRect> rects = page.GetMergedRects(0, 4);
        Assert.Equal(2, rects.Count);
        Assert.Equal(new PdfRect(5, 110, 35, 100), rects[0]);
        Assert.Equal(new PdfRect(5, 90, 10, 80), rects[1]);
    }

    [Fact]
    public void GeometryDoesNotMergeColumnsWhenReadingOrderMovesBackwards()
    {
        var page = new PdfTextPage(0, new PdfRect(0, 120, 200, 0),
        [
            Character(0, 'A', 100, 110),
            Character(1, 'B', 0, 110),
        ]);

        IReadOnlyList<PdfRect> rects = page.GetMergedRects(0, 2);
        Assert.Equal(2, rects.Count);
        Assert.Equal(new PdfRect(100, 110, 110, 100), rects[0]);
        Assert.Equal(new PdfRect(0, 110, 10, 100), rects[1]);
    }

    [Fact]
    public void GeometryMergesOverlappingIntervalsAfterCharacterSplitting()
    {
        var page = new PdfTextPage(0, new PdfRect(0, 120, 200, 0),
        [
            Character(0, 'A', 0, 110),
            new PdfTextCharacter(
                1,
                ' ',
                new PdfRect(10, 110, 30, 100),
                new PdfRect(10, 111, 30, 99)),
            Character(2, 'B', 15, 110),
        ]);

        PdfRect rect = Assert.Single(page.GetMergedRects(0, 3));
        Assert.Equal(new PdfRect(0, 110, 30, 100), rect);
    }

    [Fact]
    public void BackwardCrossPageRangeSelectsWholeIntermediatePages()
    {
        var range = new PdfTextRange(
            new PdfTextPosition(2, 4),
            new PdfTextPosition(0, 3)).Normalize();

        Assert.Equal((3, 7), range.GetPageRange(0, 10));
        Assert.Equal((0, 10), range.GetPageRange(1, 10));
        Assert.Equal((0, 4), range.GetPageRange(2, 10));
        Assert.Null(range.GetPageRange(3, 10));
    }

    [Fact]
    public void SearchReturnsAllMatchesWithSingleLineContext()
    {
        PdfTextPage page = CreatePage("Alpha\r\nbeta alpha gamma alpha");
        IReadOnlyList<PdfSearchResult> results = PdfTextSearch.SearchPage(page, "alpha");

        Assert.Equal(3, results.Count);
        Assert.Equal([0, 12, 24], results.Select(result => result.CharacterIndex).ToArray());
        Assert.All(results, result => Assert.DoesNotContain('\r', result.Context));
        Assert.Single(PdfTextSearch.SearchPage(page, "Alpha", matchCase: true));
    }

    private static PdfTextPage CreatePage(string text) => new(
        0,
        new PdfRect(0, 120, 600, 0),
        text.EnumerateRunes().Select((rune, index) => new PdfTextCharacter(
            index,
            (uint)rune.Value,
            new PdfRect(index * 10, 110, index * 10 + 10, 100),
            new PdfRect(index * 10, 111, index * 10 + 10, 99))).ToArray());

    private static PdfTextCharacter Character(int index, char value, double x, double top) => new(
        index,
        value,
        new PdfRect(x, top, x + 10, top - 10),
        new PdfRect(x, top + 1, x + 10, top - 11));
}
