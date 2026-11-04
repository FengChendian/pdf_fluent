namespace pdf_fluent.Models;

public readonly record struct PdfRect(double L, double T, double R, double B)
{
    public double Width => Math.Max(0, R - L);
    public double Height => Math.Max(0, T - B);
    public double CenterX => (L + R) / 2;
    public double CenterY => (T + B) / 2;

    public bool Contains(double x, double y, double xTolerance = 0, double yTolerance = 0) =>
        x >= L - xTolerance && x <= R + xTolerance &&
        y >= B - yTolerance && y <= T + yTolerance;

    public bool Intersects(PdfRect other) =>
        L < other.R && R > other.L && B < other.T && T > other.B;

    public PdfRect Union(PdfRect other) => new(
        Math.Min(L, other.L),
        Math.Max(T, other.T),
        Math.Max(R, other.R),
        Math.Min(B, other.B));
}
