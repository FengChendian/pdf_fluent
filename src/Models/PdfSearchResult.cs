namespace pdf_fluent.Models;

public sealed class PdfSearchResult(
    int pageIndex,
    int characterIndex,
    int characterCount,
    string context)
{
    public int PageIndex { get; } = pageIndex;
    public int PageNumber => PageIndex + 1;
    public int CharacterIndex { get; } = characterIndex;
    public int CharacterCount { get; } = characterCount;
    public string Context { get; } = context;
    public string PageLabel => $"第 {PageNumber} 页";
}
