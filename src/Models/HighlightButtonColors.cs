using Avalonia.Media;

namespace pdf_fluent.Models;

/// <summary>Contrasting, hue-preserving pen colors for the white reader toolbar.</summary>
internal static class HighlightButtonColors
{
    // WCAG non-text contrast requires 3:1. Use 4.5:1 for the small pen's thin strokes.
    private const double TargetContrast = 4.5;

    public static Color ForegroundFor(Color highlight)
    {
        Color background = OverWhite(highlight);
        Color source = Color.FromRgb(highlight.R, highlight.G, highlight.B);
        if (ContrastRatio(source, background) >= TargetContrast) return source;

        Color endpoint = ContrastRatio(Colors.Black, background) >= ContrastRatio(Colors.White, background)
            ? Colors.Black : Colors.White;
        HslColor hsl = source.ToHsl();
        double targetLightness = endpoint == Colors.Black ? 0 : 1;
        double low = 0, high = 1;
        Color result = endpoint;
        // Keep HSL hue and saturation while adjusting lightness. Search for the smallest
        // change meeting the target, including final 8-bit channel rounding.
        for (int i = 0; i < 20; i++)
        {
            double amount = (low + high) / 2;
            Color candidate = new HslColor(1, hsl.H, hsl.S,
                hsl.L + (targetLightness - hsl.L) * amount).ToRgb();
            if (ContrastRatio(candidate, background) >= TargetContrast)
            {
                high = amount;
                result = candidate;
            }
            else low = amount;
        }
        return result;
    }

    private static Color OverWhite(Color color) =>
        Mix(Colors.White, Color.FromRgb(color.R, color.G, color.B), color.A / 255d);

    private static Color Mix(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));

    // https://www.w3.org/TR/WCAG22/#dfn-relative-luminance
    private static double ContrastRatio(Color first, Color second)
    {
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(Color color)
    {
        static double Linear(byte channel)
        {
            double value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }
}
