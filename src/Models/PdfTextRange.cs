namespace pdf_fluent.Models;

public readonly record struct PdfTextPosition(int PageIndex, int CharacterIndex)
{
    public static int Compare(PdfTextPosition left, PdfTextPosition right)
    {
        int pageComparison = left.PageIndex.CompareTo(right.PageIndex);
        return pageComparison != 0
            ? pageComparison
            : left.CharacterIndex.CompareTo(right.CharacterIndex);
    }
}

public readonly record struct PdfTextRange(PdfTextPosition Start, PdfTextPosition End)
{
    public bool IsEmpty => PdfTextPosition.Compare(Start, End) >= 0;

    public PdfTextRange Normalize() =>
        PdfTextPosition.Compare(Start, End) <= 0 ? this : new PdfTextRange(End, Start);

    public (int Start, int Count)? GetPageRange(int pageIndex, int characterCount)
    {
        PdfTextRange range = Normalize();
        if (range.IsEmpty || pageIndex < range.Start.PageIndex || pageIndex > range.End.PageIndex)
            return null;

        int start = pageIndex == range.Start.PageIndex ? range.Start.CharacterIndex : 0;
        int end = pageIndex == range.End.PageIndex ? range.End.CharacterIndex : characterCount;
        start = Math.Clamp(start, 0, characterCount);
        end = Math.Clamp(end, 0, characterCount);
        return end > start ? (start, end - start) : null;
    }
}
