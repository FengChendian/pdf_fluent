namespace pdf_fluent.Models;

public sealed class RecentDocument
{
    public string FilePath { get; set; } = string.Empty;
    public int PageCount { get; set; }
    public DateTimeOffset LastOpened { get; set; }
}
