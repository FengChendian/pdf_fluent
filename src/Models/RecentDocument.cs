namespace pdf_fluent.Models;

public sealed class RecentDocument
{
    public string FilePath { get; set; } = string.Empty;
    public int PageCount { get; set; }
    public DateTimeOffset LastOpened { get; set; }
    public double ScrollOffsetX { get; set; }
    public double ScrollOffsetY { get; set; }
    // Version 0 used 72 DIP of top padding; version 1 uses 24 DIP.
    public int ScrollLayoutVersion { get; set; }
    public bool IsUnread { get; set; }
    public byte[]? Thumbnail { get; set; }
}
