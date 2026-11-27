using Avalonia.Media;
using pdf_fluent.Models;
using Xunit;

namespace pdf_fluent.Tests;

public sealed class HighlightButtonColorsTests
{
    [Fact]
    public void PenStaysInTheSameHueAndContrastsAcrossTheRgbAndAlphaRanges()
    {
        foreach (byte alpha in new byte[] { 0, 16, 128, 255 })
        for (int r = 0; r <= 255; r += 51)
        for (int g = 0; g <= 255; g += 51)
        for (int b = 0; b <= 255; b += 51)
        {
            Color highlight = Color.FromArgb(alpha, (byte)r, (byte)g, (byte)b);
            Color pen = HighlightButtonColors.ForegroundFor(highlight);
            Assert.Equal(255, pen.A);
            Assert.InRange(ContrastOnWhite(pen, highlight), 4.5, 21);
            if (r == g && g == b)
            {
                Assert.Equal(pen.R, pen.G);
                Assert.Equal(pen.G, pen.B);
            }
            else
            {
                double difference = Math.Abs(highlight.ToHsv().H - pen.ToHsv().H);
                int chroma = Math.Max(pen.R, Math.Max(pen.G, pen.B)) - Math.Min(pen.R, Math.Min(pen.G, pen.B));
                // Near-black/white colors have very few chromatic channel steps left;
                // allow one 8-bit channel step's hue error rather than a fixed angle.
                if (chroma > 0)
                    Assert.InRange(Math.Min(difference, 360 - difference), 0, Math.Max(2, 60d / chroma));
            }
        }
    }

    // Independent WCAG oracle, including the translucent background's rendered color.
    internal static double ContrastOnWhite(Color pen, Color highlight)
    {
        double opacity = highlight.A / 255d;
        byte Composite(byte channel) => (byte)Math.Round(channel * opacity + 255 * (1 - opacity));
        static double Luminance(Color color)
        {
            double[] channels = { color.R / 255d, color.G / 255d, color.B / 255d };
            double[] linear = channels.Select(c => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4)).ToArray();
            return linear[0] * 0.2126 + linear[1] * 0.7152 + linear[2] * 0.0722;
        }
        double foreground = Luminance(pen);
        double background = Luminance(Color.FromRgb(Composite(highlight.R), Composite(highlight.G), Composite(highlight.B)));
        return (Math.Max(foreground, background) + 0.05) / (Math.Min(foreground, background) + 0.05);
    }
}
