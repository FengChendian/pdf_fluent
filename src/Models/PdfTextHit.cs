namespace pdf_fluent.Models;

public readonly record struct PdfTextHit(int CharacterIndex, int CaretIndex, bool IsOnText);
