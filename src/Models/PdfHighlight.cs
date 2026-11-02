using Avalonia.Media;

namespace pdf_fluent.Models;

public sealed class PdfHighlight(int pageIndex, IReadOnlyList<PdfRect> rects, Color color)
{
    public int PageIndex { get; } = pageIndex;
    public IReadOnlyList<PdfRect> Rects { get; } = rects;
    public Color Color { get; } = color;
    public byte Alpha => Color.A;
}
