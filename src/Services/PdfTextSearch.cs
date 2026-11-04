using System.Text;
using pdf_fluent.Models;

namespace pdf_fluent.Services;

public static class PdfTextSearch
{
    private const int ContextRadius = 42;

    public static IReadOnlyList<PdfSearchResult> SearchPage(
        PdfTextPage page,
        string query,
        bool matchCase = false)
    {
        if (string.IsNullOrWhiteSpace(query) || page.Text.Length == 0) return [];

        StringComparison comparison = matchCase
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        var results = new List<PdfSearchResult>();
        int offset = 0;
        while (offset <= page.Text.Length - query.Length)
        {
            int match = page.Text.IndexOf(query, offset, comparison);
            if (match < 0) break;

            int characterStart = page.CharacterIndexFromTextOffset(match);
            int characterEnd = page.CharacterIndexFromTextOffset(match + query.Length);
            int characterCount = Math.Max(1, characterEnd - characterStart);
            results.Add(new PdfSearchResult(
                page.PageIndex,
                characterStart,
                characterCount,
                CreateContext(page.Text, match, query.Length)));
            offset = match + query.Length;
        }
        return results;
    }

    private static string CreateContext(string text, int match, int length)
    {
        int start = Math.Max(0, match - ContextRadius);
        int end = Math.Min(text.Length, match + length + ContextRadius);
        string context = SingleLine(text[start..end]);
        return (start > 0 ? "…" : string.Empty) + context + (end < text.Length ? "…" : string.Empty);
    }

    private static string SingleLine(string value)
    {
        var result = new StringBuilder(value.Length);
        bool previousWasWhitespace = false;
        foreach (char character in value)
        {
            bool whitespace = char.IsWhiteSpace(character);
            if (whitespace && previousWasWhitespace) continue;
            result.Append(whitespace ? ' ' : character);
            previousWasWhitespace = whitespace;
        }
        return result.ToString().Trim();
    }
}
