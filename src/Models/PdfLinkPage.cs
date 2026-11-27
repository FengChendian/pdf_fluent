using Avalonia;

namespace pdf_fluent.Models;

/// <summary>Link geometry without the per-character text model used by selection.</summary>
public sealed class PdfLinkPage(int pageIndex, PdfRect cropBox, IReadOnlyList<PdfLink> links, int rotation = 0)
{
    public int PageIndex { get; } = pageIndex;
    public PdfRect CropBox { get; } = cropBox;
    public int Rotation { get; } = rotation;
    public IReadOnlyList<PdfLink> Links { get; } = links.ToArray();

    public PdfLink? HitTestLink(Point pagePoint)
    {
        Point pdf = PdfLinkCoordinates.ToPdf(pagePoint, CropBox, Rotation);
        for (int index = Links.Count - 1; index >= 0; index--)
        {
            PdfLink link = Links[index];
            foreach (PdfLinkRegion region in link.Regions)
                if (region.Contains(pdf)) return link.Target is null ? null : link;
        }
        return null;
    }
}
