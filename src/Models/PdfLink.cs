using Avalonia;

namespace pdf_fluent.Models;

public enum PdfDestinationView { Unknown, Xyz, Fit, FitH, FitV, FitR, FitB, FitBH, FitBV }

/// <summary>PDF coordinates and nullable XYZ parameters are retained until navigation.</summary>
public sealed record PdfDestination(
    int PageIndex, PdfRect PageBox, int Rotation = 0,
    PdfDestinationView View = PdfDestinationView.Xyz,
    double? X = null, double? Y = null, double? Zoom = null, PdfRect? FitRect = null);

public sealed record PdfLinkTarget(Uri? Uri = null, PdfDestination? Destination = null)
{
    public string DisplayText => Uri?.AbsoluteUri ??
        (Destination is { } destination ? $"跳转到第 {destination.PageIndex + 1} 页" : string.Empty);

    public static PdfLinkTarget? FromUri(string value)
    {
        if (value.Any(char.IsControl) || !System.Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)) return null;
        return uri.Scheme is "http" or "https" or "mailto" ? new PdfLinkTarget(Uri: uri) : null;
    }
}

/// <summary>Four points in perimeter order; separate quads never turn line gaps into links.</summary>
public readonly record struct PdfLinkRegion(Point A, Point B, Point C, Point D)
{
    public static PdfLinkRegion FromCorners(Point a, Point b, Point c, Point d)
    {
        // Producers use both the PDF perimeter order and Acrobat's Z order.
        Point[] points = [a, b, c, d];
        double x = points.Average(point => point.X), y = points.Average(point => point.Y);
        Array.Sort(points, (left, right) => Math.Atan2(left.Y - y, left.X - x)
            .CompareTo(Math.Atan2(right.Y - y, right.X - x)));
        return new PdfLinkRegion(points[0], points[1], points[2], points[3]);
    }

    public static PdfLinkRegion FromRect(PdfRect rect) => new(
        new Point(rect.L, rect.T), new Point(rect.R, rect.T),
        new Point(rect.R, rect.B), new Point(rect.L, rect.B));

    public bool Contains(Point point)
    {
        double ab = Cross(A, B, point), bc = Cross(B, C, point);
        double cd = Cross(C, D, point), da = Cross(D, A, point);
        if (Math.Abs(Cross(A, B, C)) + Math.Abs(Cross(A, C, D)) < 1e-8) return false;
        return (ab >= 0 && bc >= 0 && cd >= 0 && da >= 0) ||
               (ab <= 0 && bc <= 0 && cd <= 0 && da <= 0);
    }

    private static double Cross(Point a, Point b, Point p) =>
        (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
}

/// <summary>A null target preserves an unsupported annotation's hit area over auto-detected URLs.</summary>
public sealed record PdfLink(IReadOnlyList<PdfLinkRegion> Regions, PdfLinkTarget? Target);

internal static class PdfLinkCoordinates
{
    public static Point ToPage(Point pdf, PdfRect box, int rotation) => (rotation & 3) switch
    {
        1 => new Point(pdf.Y - box.B, pdf.X - box.L),
        2 => new Point(box.R - pdf.X, pdf.Y - box.B),
        3 => new Point(box.T - pdf.Y, box.R - pdf.X),
        _ => new Point(pdf.X - box.L, box.T - pdf.Y),
    };

    public static Point ToPdf(Point page, PdfRect box, int rotation) => (rotation & 3) switch
    {
        1 => new Point(box.L + page.Y, box.B + page.X),
        2 => new Point(box.R - page.X, box.B + page.Y),
        3 => new Point(box.R - page.Y, box.T - page.X),
        _ => new Point(box.L + page.X, box.T - page.Y),
    };
}
