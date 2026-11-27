using Avalonia.Media;

namespace pdf_fluent.Models;

public static class HighlightPalette
{
    // HSB 调色：H = 色相（0–360°），S = 饱和度（0–100%），B = 明度（0–100%）。
    // Alpha 独立控制不透明度（0–255）；96 约为 38%，叠在白纸上会让颜色更柔和。
    public const byte Alpha = 255;

    public static Color Red { get; } = FromHsb(h: 353.10, s: 37.99, b: 100);
    public static Color Orange { get; } = FromHsb(h: 28.47, s: 49.58, b: 100);
    public static Color Yellow { get; } = FromHsb(h: 53.88, s: 76.86, b: 100.00);
    public static Color Green { get; } = FromHsb(h: 143.39, s: 29.80, b: 100);
    public static Color Blue { get; } = FromHsb(h: 216.00, s: 37.61, b: 100);

    // Avalonia 把 HSB 称为 HSV，其中 V 与 B 相同。
    private static Color FromHsb(double h, double s, double b) =>
        new HsvColor(Alpha / 255d, h, s / 100, b / 100).ToRgb();
}
