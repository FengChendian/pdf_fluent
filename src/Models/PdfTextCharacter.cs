namespace pdf_fluent.Models;

public readonly record struct PdfTextCharacter(
    int Index,
    uint Unicode,
    PdfRect Bounds,
    PdfRect LooseBounds,
    bool IsGenerated = false)
{
    public bool HasGeometry => Bounds is { Width: > 0, Height: > 0 };
}
