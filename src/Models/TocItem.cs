namespace pdf_fluent.Models;

public sealed class TocItem
{
    public string Title { get; set; } = string.Empty;
    public int PageIndex { get; set; }
    public List<TocItem> Children { get; } = [];
}
