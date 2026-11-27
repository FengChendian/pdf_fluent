using System.Runtime.InteropServices;
using Avalonia;
using PDFiumCore;
using pdf_fluent.Models;

namespace pdf_fluent.Services;

public partial class PDFiumService
{
    // Called under _pageLock. Only managed values leave this method.
    private static List<PdfLink> ReadPageLinks(
        FpdfDocumentT document, FpdfPageT page, FpdfTextpageT text, CancellationToken cancellation)
    {
        var links = new List<PdfLink>();
        var webLinks = fpdf_text.FPDFLinkLoadWebLinks(text);
        if (webLinks is not null)
        {
            try
            {
                for (int i = 0, count = fpdf_text.FPDFLinkCountWebLinks(webLinks); i < count; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    ushort probe = 0;
                    int length = fpdf_text.FPDFLinkGetURL(webLinks, i, ref probe, 0);
                    if (length <= 1) continue;
                    var buffer = new ushort[length];
                    int written = fpdf_text.FPDFLinkGetURL(webLinks, i, ref buffer[0], length);
                    string url = new(buffer.Take(Math.Clamp(written - 1, 0, length)).Select(value => (char)value).ToArray());
                    PdfLinkTarget? target = PdfLinkTarget.FromUri(url);
                    if (target is null) continue;
                    var regions = new List<PdfLinkRegion>();
                    for (int r = 0, rectCount = fpdf_text.FPDFLinkCountRects(webLinks, i); r < rectCount; r++)
                    {
                        double l = 0, t = 0, right = 0, b = 0;
                        if (fpdf_text.FPDFLinkGetRect(webLinks, i, r, ref l, ref t, ref right, ref b) != 0)
                            regions.Add(PdfLinkRegion.FromRect(new PdfRect(l, t, right, b)));
                    }
                    if (regions.Count > 0) links.Add(new PdfLink(regions.ToArray(), target));
                }
            }
            finally { fpdf_text.FPDFLinkCloseWebLinks(webLinks); }
        }

        // Annotation order is Z order. Appending them makes explicit links win
        // over text scanning; HitTestLink walks from the topmost annotation down.
        var targetPages = new Dictionary<int, (PdfRect Box, int Rotation)>();
        for (int i = 0, count = fpdf_annot.FPDFPageGetAnnotCount(page); i < count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var annotation = fpdf_annot.FPDFPageGetAnnot(page, i);
            if (annotation is null) continue;
            try
            {
                if (fpdf_annot.FPDFAnnotGetSubtype(annotation) != 2) continue; // Link
                if ((fpdf_annot.FPDFAnnotGetFlags(annotation) & (1 | 2 | 32)) != 0) continue;
                var link = fpdf_annot.FPDFAnnotGetLink(annotation);
                if (link is null) continue;
                var regions = new List<PdfLinkRegion>();
                for (int q = 0, countQuads = fpdf_doc.FPDFLinkCountQuadPoints(link); q < countQuads; q++)
                {
                    using var quad = new FS_QUADPOINTSF();
                    if (fpdf_doc.FPDFLinkGetQuadPoints(link, q, quad) != 0)
                        regions.Add(PdfLinkRegion.FromCorners(new Point(quad.X1, quad.Y1), new Point(quad.X2, quad.Y2),
                            new Point(quad.X4, quad.Y4), new Point(quad.X3, quad.Y3)));
                }
                if (regions.Count == 0)
                {
                    using var rect = new FS_RECTF_();
                    if (fpdf_doc.FPDFLinkGetAnnotRect(link, rect) != 0)
                        regions.Add(PdfLinkRegion.FromRect(new PdfRect(rect.Left, rect.Top, rect.Right, rect.Bottom)));
                }
                if (regions.Count == 0) continue;
                PdfLinkTarget? target = null;
                var destination = fpdf_doc.FPDFLinkGetDest(document, link);
                var action = fpdf_doc.FPDFLinkGetAction(link);
                if (destination is null && action is not null && fpdf_doc.FPDFActionGetType(action) == PDFACTION_GOTO)
                    destination = fpdf_doc.FPDFActionGetDest(document, action);
                if (destination is not null)
                {
                    if (ReadDestination(document, destination, targetPages) is { } value)
                        target = new PdfLinkTarget(Destination: value);
                }
                else if (action is not null && fpdf_doc.FPDFActionGetType(action) == 3) // URI
                {
                    ulong length = fpdf_doc.FPDFActionGetURIPath(document, action, IntPtr.Zero, 0);
                    if (length > 1 && length <= int.MaxValue)
                    {
                        IntPtr buffer = Marshal.AllocHGlobal((int)length);
                        try
                        {
                            ulong written = fpdf_doc.FPDFActionGetURIPath(document, action, buffer, length);
                            if (written > 1 && written <= length)
                                target = PdfLinkTarget.FromUri(Marshal.PtrToStringUTF8(buffer, (int)written - 1) ?? "");
                        }
                        finally { Marshal.FreeHGlobal(buffer); }
                    }
                }
                links.Add(new PdfLink(regions.ToArray(), target));
            }
            finally { fpdf_annot.FPDFPageCloseAnnot(annotation); }
        }
        return links;
    }

    private static PdfDestination? ReadDestination(FpdfDocumentT document, FpdfDestT destination,
        Dictionary<int, (PdfRect Box, int Rotation)> targetPages)
    {
        int index = fpdf_doc.FPDFDestGetDestPageIndex(document, destination);
        if (index < 0 || index >= fpdfview.FPDF_GetPageCount(document)) return null;
        if (!targetPages.TryGetValue(index, out var geometry))
        {
            var page = fpdfview.FPDF_LoadPage(document, index);
            if (page is null) return null;
            try { geometry = (GetVisiblePageBox(document, index, page), fpdf_edit.FPDFPageGetRotation(page)); }
            finally { fpdfview.FPDF_ClosePage(page); }
            targetPages[index] = geometry;
        }

        var parameters = new float[4];
        ulong count = 0;
        var view = (PdfDestinationView)fpdf_doc.FPDFDestGetView(destination, ref count, ref parameters[0]);
        double? x = null, y = null, zoom = null;
        PdfRect? fit = null;
        if (view == PdfDestinationView.Xyz)
        {
            int hasX = 0, hasY = 0, hasZoom = 0;
            float px = 0, py = 0, pz = 0;
            if (fpdf_doc.FPDFDestGetLocationInPage(destination, ref hasX, ref hasY, ref hasZoom, ref px, ref py, ref pz) != 0)
            {
                if (hasX != 0 && float.IsFinite(px)) x = px;
                if (hasY != 0 && float.IsFinite(py)) y = py;
                if (hasZoom != 0 && float.IsFinite(pz) && pz > 0) zoom = pz;
            }
        }
        else if (count >= 1 && float.IsFinite(parameters[0]))
        {
            if (view is PdfDestinationView.FitH or PdfDestinationView.FitBH) y = parameters[0];
            if (view is PdfDestinationView.FitV or PdfDestinationView.FitBV) x = parameters[0];
            if (view == PdfDestinationView.FitR && count == 4 && parameters.All(float.IsFinite))
                fit = new PdfRect(parameters[0], parameters[3], parameters[2], parameters[1]);
        }
        return new PdfDestination(index, geometry.Box, geometry.Rotation, view, x, y, zoom, fit);
    }
}
