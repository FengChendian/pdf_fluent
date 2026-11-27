using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace pdf_fluent.Controls;

/// <summary>A compact saturation/value surface and hue strip, without bitmap allocations.</summary>
public sealed class HighlightColorSpectrum : Control
{
    public static readonly StyledProperty<Color> ColorProperty =
        AvaloniaProperty.Register<HighlightColorSpectrum, Color>(nameof(Color), Colors.Yellow);

    private double _hue = 60;
    private double _saturation = 1;
    private double _value = 1;
    private bool _updating;
    private bool _draggingHue;

    public Color Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public event EventHandler? ColorChanged;

    public HighlightColorSpectrum()
    {
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Cross);
        MinHeight = 146;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ColorProperty) return;
        if (!_updating)
        {
            var hsv = Color.ToHsv();
            if (hsv.S > 0) _hue = hsv.H;
            _saturation = hsv.S;
            _value = hsv.V;
        }
        InvalidateVisual();
        ColorChanged?.Invoke(this, EventArgs.Empty);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        double height = Math.Max(1, Bounds.Height - 30);
        var surface = new Rect(0, 0, Bounds.Width, height);
        context.DrawRectangle(new SolidColorBrush(new HsvColor(1, _hue, 1, 1).ToRgb()), null, surface, 6, 6);
        using (context.PushClip(new RoundedRect(surface, 6)))
        {
            context.DrawRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = [new GradientStop(Colors.White, 0), new GradientStop(Colors.Transparent, 1)],
            }, null, surface);
            context.DrawRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = [new GradientStop(Colors.Transparent, 0), new GradientStop(Colors.Black, 1)],
            }, null, surface);
        }
        var marker = new Point(_saturation * Bounds.Width, (1 - _value) * height);
        context.DrawEllipse(null, new Pen(Brushes.Black, 3), marker, 5, 5);
        context.DrawEllipse(null, new Pen(Brushes.White, 2), marker, 5, 5);

        var stops = new GradientStops();
        for (int i = 0; i <= 6; i++) stops.Add(new GradientStop(new HsvColor(1, i * 60, 1, 1).ToRgb(), i / 6d));
        context.DrawRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = stops,
        }, null, new Rect(0, height + 15, Bounds.Width, 10), 5, 5);
        var hueMarker = new Point(_hue / 360 * Bounds.Width, height + 20);
        context.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(Color.Parse("#7C8192")), 1), hueMarker, 6, 6);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        _draggingHue = e.GetPosition(this).Y > Bounds.Height - 30;
        e.Pointer.Capture(this);
        UpdateFromPointer(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.Pointer.Captured == this) UpdateFromPointer(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.Pointer.Captured == this) e.Pointer.Capture(null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        double step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.05 : 0.01;
        switch (e.Key)
        {
            case Key.Left: _saturation = Math.Max(0, _saturation - step); break;
            case Key.Right: _saturation = Math.Min(1, _saturation + step); break;
            case Key.Up: _value = Math.Min(1, _value + step); break;
            case Key.Down: _value = Math.Max(0, _value - step); break;
            case Key.PageUp: _hue = (_hue + 5) % 360; break;
            case Key.PageDown: _hue = (_hue + 355) % 360; break;
            default: return;
        }
        PublishColor();
        e.Handled = true;
    }

    private void UpdateFromPointer(Point point)
    {
        if (_draggingHue) _hue = Math.Clamp(point.X / Math.Max(1, Bounds.Width), 0, 1) * 359.99;
        else
        {
            _saturation = Math.Clamp(point.X / Math.Max(1, Bounds.Width), 0, 1);
            _value = 1 - Math.Clamp(point.Y / Math.Max(1, Bounds.Height - 30), 0, 1);
        }
        PublishColor();
    }

    private void PublishColor()
    {
        _updating = true;
        try { Color = new HsvColor(Color.A / 255d, _hue, _saturation, _value).ToRgb(); }
        finally { _updating = false; }
        InvalidateVisual();
    }
}
