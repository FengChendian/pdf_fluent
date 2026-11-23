using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace pdf_fluent.Controls;

/// <summary>Draws a browser tab with concave feet shared with neighboring tabs.</summary>
public sealed class EdgeTabChrome : Control
{
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<EdgeTabChrome, IBrush?>(nameof(Fill));
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<EdgeTabChrome, bool>(nameof(IsActive));
    private const double TabRadius = 10;
    private StreamGeometry? _geometry;
    private Size _geometrySize;

    static EdgeTabChrome() => AffectsRender<EdgeTabChrome>(FillProperty, IsActiveProperty, BoundsProperty);
    public EdgeTabChrome() => IsHitTestVisible = false;
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        // Hover and selection share the same outline; their fill distinguishes them.
        if (_geometry is null || _geometrySize != Bounds.Size)
        {
            _geometrySize = Bounds.Size;
            _geometry = CreateGeometry(Bounds.Size);
        }
        context.DrawGeometry(Fill, null, _geometry);
    }

    private static StreamGeometry CreateGeometry(Size size)
    {
        var geometry = new StreamGeometry();
        double w = size.Width, h = size.Height;
        if (w <= 0 || h <= 0) return geometry;
        double r = Math.Clamp(TabRadius, 0, Math.Min(w / 4, h / 2));
        const double k = 0.5522847498307936;
        using var context = geometry.Open();
        context.BeginFigure(new Point(0, h), true);
        context.CubicBezierTo(new Point(k * r, h), new Point(r, h - r + k * r), new Point(r, h - r));
        context.LineTo(new Point(r, r));
        context.CubicBezierTo(new Point(r, r - k * r), new Point(2 * r - k * r, 0), new Point(2 * r, 0));
        context.LineTo(new Point(w - 2 * r, 0));
        context.CubicBezierTo(new Point(w - 2 * r + k * r, 0), new Point(w - r, r - k * r), new Point(w - r, r));
        context.LineTo(new Point(w - r, h - r));
        context.CubicBezierTo(new Point(w - r, h - r + k * r), new Point(w - k * r, h), new Point(w, h));
        context.EndFigure(true);
        return geometry;
    }
}
