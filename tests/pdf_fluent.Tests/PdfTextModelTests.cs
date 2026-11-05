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
    public void HitTestSwitchesColumnsAtTheGutterMidline()
    {
        var page = new PdfTextPage(0, new PdfRect(0, 200, 200, 0),
        [
            Character(0, 'a', 0, 110),
            Character(1, 'b', 10, 110),
            Character(2, 'A', 0, 90),
            Character(3, 'B', 10, 90),
            Character(4, 'x', 100, 110),
            Character(5, 'y', 110, 110),
            Character(6, 'X', 100, 90),
            Character(7, 'Y', 110, 90),
        ]);

        // The left column ends at x=20 and the right column starts at x=100,
        // so the gutter midline is x=60.
        PdfTextHit left = Assert.NotNull(page.HitTest(40, 105, nearestOnLine: true));
        Assert.Equal(1, left.CharacterIndex);
        Assert.Equal(2, left.CaretIndex);
        Assert.Equal(1, Assert.NotNull(page.HitTest(59.9, 105, nearestOnLine: true)).CharacterIndex);

        PdfTextHit right = Assert.NotNull(page.HitTest(60.1, 105, nearestOnLine: true));
        Assert.Equal(4, right.CharacterIndex);
        Assert.Equal(4, right.CaretIndex);

        // At the exact midline the left column wins: only past the midline
        // may the caret enter the right column.
        Assert.Equal(2, Assert.NotNull(page.HitTest(60, 105, nearestOnLine: true)).CaretIndex);
        Assert.Null(page.HitTest(40, 105));
    }

    [Fact]
    public void HitTestKeepsShortLastLineInTheLeftColumn()
    {
        var page = new PdfTextPage(0, new PdfRect(0, 240, 240, 0),
        [
            Character(0, 'S', 0, 150),
            Character(1, 'R', 100, 150),
            Character(2, 'a', 0, 130),
            Character(3, 'b', 10, 130),
            Character(4, 'c', 20, 130),
            Character(5, 'd', 30, 130),
            Character(6, 'e', 40, 130),
            Character(7, 'X', 100, 130),
            Character(8, 'a', 0, 110),
            Character(9, 'b', 10, 110),
            Character(10, 'c', 20, 110),
            Character(11, 'd', 30, 110),
            Character(12, 'e', 40, 110),
            Character(13, 'X', 100, 110),
        ]);

        // The short line's own gap is [10,100] (midline 55), but the gutter
        // shared with the surrounding lines is [50,100] (midline 75). A
        // pointer at x=70 is past the short line's local midpoint yet still
        // on the left side of the real gutter, so it must stay in the left
        // column instead of snapping to the right column.
        PdfTextHit left = Assert.NotNull(page.HitTest(70, 145, nearestOnLine: true));
        Assert.Equal(0, left.CharacterIndex);
        Assert.Equal(1, left.CaretIndex);

        PdfTextHit right = Assert.NotNull(page.HitTest(80, 145, nearestOnLine: true));
        Assert.Equal(1, right.CharacterIndex);
        Assert.Equal(1, right.CaretIndex);
    }

    [Fact]
    public void HitTestIgnoresLeadingSpaceWhenLocatingTheGutter()
    {
        var page = new PdfTextPage(0, new PdfRect(0, 240, 240, 0),
        [
            Character(0, 'L', 0, 150),
            new PdfTextCharacter(
                1,
                ' ',
                new PdfRect(100, 150, 120, 140),
                new PdfRect(100, 151, 120, 139)),
            Character(2, 'R', 120, 150),
            Character(3, 'a', 0, 130),
            Character(4, 'b', 10, 130),
            Character(5, 'c', 20, 130),
            Character(6, 'd', 30, 130),
            Character(7, 'e', 40, 130),
            Character(8, 'R', 120, 130),
            Character(9, 'a', 0, 110),
            Character(10, 'b', 10, 110),
            Character(11, 'c', 20, 110),
            Character(12, 'd', 30, 110),
            Character(13, 'e', 40, 110),
            Character(14, 'R', 120, 110),
        ]);

        // Visible glyphs leave [10,120] empty on the short line and [50,120]
        // on the longer lines, giving a real midline of x=85. The invisible
        // leading space at 100..120 must not pull the caret into the right
        // column at x=80.
        PdfTextHit left = Assert.NotNull(page.HitTest(80, 145, nearestOnLine: true));
        Assert.Equal(0, left.CharacterIndex);
        Assert.Equal(1, left.CaretIndex);

        PdfTextHit right = Assert.NotNull(page.HitTest(90, 145, nearestOnLine: true));
        Assert.Equal(2, right.CharacterIndex);
        Assert.Equal(2, right.CaretIndex);
    }

    [Fact]
    public void HitTestOnlyEntersStaggeredRightColumnPastTheMidline()
    {
        var page = new PdfTextPage(0, new PdfRect(0, 240, 200, 0),
        [
            Character(0, 'A', 0, 190),
            Character(1, 'B', 0, 150),
            Character(2, 'R', 100, 190),
            Character(3, 'S', 100, 170),
            Character(4, 'T', 100, 150),
        ]);

        // At y=165 there is only a right-column line. Left of the gutter
        // midline no left-column line contains the pointer, so the selection
        // endpoint must stay where it is rather than jump to the right.
        Assert.Null(page.HitTest(40, 165, nearestOnLine: true));

        PdfTextHit right = Assert.NotNull(page.HitTest(96, 165, nearestOnLine: true));
        Assert.Equal(3, right.CharacterIndex);
        Assert.Equal(3, right.CaretIndex);
    }

    [Fact]
    public void HitTestAllowsTextInsideColumnGutter()
    {
        var page = new PdfTextPage(0, new PdfRect(0, 240, 200, 0),
        [
            Character(0, 'A', 0, 190),
            Character(1, 'B', 0, 150),
            Character(2, 'M', 50, 170),
            Character(3, 'R', 100, 190),
            Character(4, 'S', 100, 150),
        ]);

        PdfTextHit hit = Assert.NotNull(page.HitTest(54, 165, nearestOnLine: true));
        Assert.Equal(2, hit.CharacterIndex);
        Assert.True(hit.IsOnText);
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
