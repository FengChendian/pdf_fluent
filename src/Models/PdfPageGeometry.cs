using Avalonia;

namespace pdf_fluent.Models;

/// <summary>
/// Keeps the logical page geometry used by scrolling independent of the integer
/// device-pixel rectangle used by the bitmap, overlays and pointer hit testing.
/// </summary>
internal readonly record struct PdfPageGeometry(Rect Bounds, Size LogicalSize, PixelSize PixelSize)
{
    public static PixelSize GetPixelSize(double width, double height, double scale) => new(
        Math.Max(1, checked((int)Math.Round(width * scale, MidpointRounding.AwayFromZero))),
        Math.Max(1, checked((int)Math.Round(height * scale, MidpointRounding.AwayFromZero))));

    public static PdfPageGeometry Create(Size logicalSize, double scaling, Point originInRoot)
    {
        PixelSize pixels = GetPixelSize(logicalSize.Width, logicalSize.Height, scaling);
        var offset = new Point(
            Math.Round(originInRoot.X * scaling, MidpointRounding.AwayFromZero) / scaling - originInRoot.X,
            Math.Round(originInRoot.Y * scaling, MidpointRounding.AwayFromZero) / scaling - originInRoot.Y);
        return new PdfPageGeometry(
            new Rect(offset, new Size(pixels.Width / scaling, pixels.Height / scaling)), logicalSize, pixels);
    }

    public Point ToLogical(Point point) => new(
        (point.X - Bounds.X) * LogicalSize.Width / Bounds.Width,
        (point.Y - Bounds.Y) * LogicalSize.Height / Bounds.Height);

    public Rect FromLogical(Rect rect) => new(
        Bounds.X + rect.X * Bounds.Width / LogicalSize.Width,
        Bounds.Y + rect.Y * Bounds.Height / LogicalSize.Height,
        rect.Width * Bounds.Width / LogicalSize.Width,
        rect.Height * Bounds.Height / LogicalSize.Height);
}
