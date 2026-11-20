using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace pdf_fluent.Controls;

internal readonly record struct PdfHighlightDrawItem(
    Rect Bounds,
    Color Color,
    double CornerRadius,
    bool Selected);

internal sealed class PdfHighlightDrawOperation(
    IReadOnlyList<PdfHighlightDrawItem> items,
    Rect bounds) : ICustomDrawOperation
{
    private static readonly Color SelectedColor = Color.FromArgb(230, 108, 92, 231);

    public Rect Bounds { get; } = bounds;

    public bool HitTest(Point p) => false;

    public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);

    public void Dispose()
    {
    }

    public void Render(ImmediateDrawingContext context)
    {
        if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } leaseFeature)
        {
            DrawFallback(context);
            return;
        }

        using var lease = leaseFeature.Lease();
        Render(lease.SkCanvas, lease.CurrentOpacity);
    }

    internal void Render(SKCanvas canvas, double opacity)
    {
        canvas.Save();
        try
        {
            using var paint = new SKPaint
            {
                IsAntialias = true,
                BlendMode = SKBlendMode.Multiply,
            };
            foreach (PdfHighlightDrawItem item in items)
            {
                var rect = SKRect.Create(
                    (float)item.Bounds.X,
                    (float)item.Bounds.Y,
                    (float)item.Bounds.Width,
                    (float)item.Bounds.Height);
                float radius = (float)item.CornerRadius;
                paint.Style = SKPaintStyle.Fill;
                paint.Color = ToSkColor(item.Color, opacity);
                canvas.DrawRoundRect(rect, radius, radius, paint);

                if (!item.Selected) continue;
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = 1.5f;
                paint.Color = ToSkColor(SelectedColor, opacity);
                canvas.DrawRoundRect(rect, radius, radius, paint);
            }
        }
        finally
        {
            canvas.Restore();
        }
    }

    private void DrawFallback(ImmediateDrawingContext context)
    {
        foreach (PdfHighlightDrawItem item in items)
        {
            ImmutablePen? pen = item.Selected
                ? new ImmutablePen(new ImmutableSolidColorBrush(SelectedColor), 1.5)
                : null;
            context.DrawRectangle(
                new ImmutableSolidColorBrush(item.Color),
                pen,
                item.Bounds,
                item.CornerRadius,
                item.CornerRadius);
        }
    }

    private static SKColor ToSkColor(Color color, double opacity) => new(
        color.R,
        color.G,
        color.B,
        (byte)Math.Clamp(color.A * opacity, 0, 255));
}
