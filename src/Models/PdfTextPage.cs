using System.Globalization;
using System.Text;

namespace pdf_fluent.Models;

public sealed class PdfTextPage
{
    private readonly int[] _textOffsets;
    private readonly TextLine[] _lines;

    public PdfTextPage(
        int pageIndex,
        PdfRect cropBox,
        IReadOnlyList<PdfTextCharacter> characters)
    {
        PageIndex = pageIndex;
        CropBox = cropBox;
        Characters = characters as PdfTextCharacter[] ?? characters.ToArray();
        if (Characters.Select(character => character.Index).Where((index, i) => index != i).Any())
            throw new ArgumentException("字符索引必须连续且按 PDFium 阅读顺序排列。", nameof(characters));

        var text = new StringBuilder(Characters.Count);
        _textOffsets = new int[Characters.Count + 1];
        foreach (PdfTextCharacter character in Characters)
        {
            _textOffsets[character.Index] = text.Length;
            if (character.Unicode == 0) continue;
            if (character.Unicode <= char.MaxValue)
                text.Append((char)character.Unicode);
            else if (character.Unicode <= 0x10FFFF)
                text.Append(char.ConvertFromUtf32((int)character.Unicode));
        }
        _textOffsets[^1] = text.Length;
        Text = text.ToString();
        _lines = BuildLines();
    }

    public int PageIndex { get; }
    public PdfRect CropBox { get; }
    public IReadOnlyList<PdfTextCharacter> Characters { get; }
    public int CharacterCount => Characters.Count;
    public string Text { get; }
    public bool HasText => CharacterCount > 0;

    public string GetText(int startIndex, int count)
    {
        if (count <= 0 || CharacterCount == 0) return string.Empty;
        int start = Math.Clamp(startIndex, 0, CharacterCount);
        int end = Math.Clamp(startIndex + count, start, CharacterCount);
        return Text[_textOffsets[start].._textOffsets[end]];
    }

    public int CharacterIndexFromTextOffset(int textOffset)
    {
        if (CharacterCount == 0) return 0;
        int offset = Math.Clamp(textOffset, 0, Text.Length);
        if (offset == Text.Length) return CharacterCount;
        int index = Array.BinarySearch(_textOffsets, offset);
        if (index < 0) index = Math.Max(0, ~index - 1);
        index = Math.Min(index, CharacterCount - 1);
        while (index + 1 < CharacterCount &&
               _textOffsets[index + 1] == offset && Characters[index].Unicode == 0)
        {
            index++;
        }
        return index;
    }

    public PdfTextHit? HitTest(
        double x,
        double y,
        bool nearestOnLine = false,
        double xTolerance = 1,
        double yTolerance = 1)
    {
        if (_lines.Length == 0) return null;

        int closestLine = FindClosestLine(y);
        int bestCharacter = -1;
        bool bestIsOnText = false;
        double bestDistance = double.PositiveInfinity;
        int first = Math.Max(0, closestLine - 2);
        int last = Math.Min(_lines.Length - 1, closestLine + 2);
        for (int lineIndex = first; lineIndex <= last; lineIndex++)
        {
            TextLine line = _lines[lineIndex];
            if (y < line.Bounds.B - yTolerance || y > line.Bounds.T + yTolerance) continue;

            int closestCharacter = line.FindClosestCharacter(x);
            for (int i = Math.Max(0, closestCharacter - 2);
                 i <= Math.Min(line.Characters.Length - 1, closestCharacter + 2);
                 i++)
            {
                PdfTextCharacter character = line.Characters[i];
                PdfRect bounds = character.LooseBounds;
                bool isOnText = bounds.Contains(x, y, xTolerance, nearestOnLine ? yTolerance : 0);
                double dx = Math.Max(Math.Max(bounds.L - x, x - bounds.R), 0);
                double dy = Math.Max(Math.Max(bounds.B - y, y - bounds.T), 0);
                double distance = dx * dx + dy * dy;
                if ((!nearestOnLine && !isOnText) || distance >= bestDistance) continue;
                bestDistance = distance;
                bestCharacter = character.Index;
                bestIsOnText = isOnText;
            }
        }

        if (bestCharacter < 0) return null;
        PdfTextCharacter selected = Characters[bestCharacter];
        int caret = x < selected.LooseBounds.CenterX ? bestCharacter : bestCharacter + 1;
        return new PdfTextHit(bestCharacter, caret, bestIsOnText);
    }

    public IReadOnlyList<PdfRect> GetMergedRects(int startIndex, int count)
    {
        if (count <= 0 || CharacterCount == 0) return [];
        int start = Math.Clamp(startIndex, 0, CharacterCount);
        int end = Math.Clamp(startIndex + count, start, CharacterCount);
        var rects = new List<PdfRect>();
        PdfRect? current = null;

        for (int index = start; index < end; index++)
        {
            PdfTextCharacter character = Characters[index];
            if (!IsVisible(character)) continue;
            PdfRect bounds = ClipToCrop(character.Bounds);
            if (bounds is not { Width: > 0, Height: > 0 }) continue;

            if (current is null)
            {
                current = bounds;
                continue;
            }

            PdfRect previous = current.Value;
            double verticalOverlap = Math.Min(previous.T, bounds.T) - Math.Max(previous.B, bounds.B);
            bool sameLine = verticalOverlap >= Math.Min(previous.Height, bounds.Height) * 0.35 &&
                            bounds.L >= previous.R - Math.Max(1, previous.Height * 0.25);
            if (sameLine)
                current = previous.Union(bounds);
            else
            {
                rects.Add(previous);
                current = bounds;
            }
        }

        if (current is not null) rects.Add(current.Value);
        return rects;
    }

    public (int Start, int Count) GetWordRange(int characterIndex)
    {
        if (characterIndex < 0 || characterIndex >= CharacterCount) return (0, 0);
        if (!IsWordCharacter(Characters[characterIndex].Unicode)) return (characterIndex, 1);

        int start = characterIndex;
        int end = characterIndex + 1;
        while (start > 0 && IsWordCharacter(Characters[start - 1].Unicode)) start--;
        while (end < CharacterCount && IsWordCharacter(Characters[end].Unicode)) end++;
        return (start, end - start);
    }

    private bool IsVisible(PdfTextCharacter character) =>
        character.HasGeometry && character.Bounds.Intersects(CropBox);

    private PdfRect ClipToCrop(PdfRect bounds) => new(
        Math.Max(bounds.L, CropBox.L),
        Math.Min(bounds.T, CropBox.T),
        Math.Min(bounds.R, CropBox.R),
        Math.Max(bounds.B, CropBox.B));

    private TextLine[] BuildLines()
    {
        PdfTextCharacter[] visible = Characters
            .Where(character => character.LooseBounds is { Width: > 0, Height: > 0 } &&
                                character.LooseBounds.Intersects(CropBox))
            .OrderByDescending(character => character.LooseBounds.CenterY)
            .ThenBy(character => character.LooseBounds.CenterX)
            .ToArray();
        var lines = new List<TextLine>();
        List<PdfTextCharacter>? current = null;
        PdfRect currentBounds = default;

        foreach (PdfTextCharacter character in visible)
        {
            PdfRect bounds = character.LooseBounds;
            if (current is not null)
            {
                double overlap = Math.Min(currentBounds.T, bounds.T) - Math.Max(currentBounds.B, bounds.B);
                if (overlap >= Math.Min(currentBounds.Height, bounds.Height) * 0.35)
                {
                    current.Add(character);
                    currentBounds = currentBounds.Union(bounds);
                    continue;
                }
            }

            if (current is not null) lines.Add(new TextLine(current, currentBounds));
            current = [character];
            currentBounds = bounds;
        }

        if (current is not null) lines.Add(new TextLine(current, currentBounds));
        return lines.OrderByDescending(line => line.Bounds.CenterY).ToArray();
    }

    private int FindClosestLine(double y)
    {
        int low = 0;
        int high = _lines.Length - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (_lines[middle].Bounds.CenterY > y) low = middle + 1;
            else high = middle - 1;
        }
        return Math.Clamp(low, 0, _lines.Length - 1);
    }

    private static bool IsWordCharacter(uint unicode)
    {
        if (unicode == '_') return true;
        if (unicode == 0 || unicode > 0x10FFFF) return false;
        UnicodeCategory category = unicode <= char.MaxValue
            ? char.GetUnicodeCategory((char)unicode)
            : CharUnicodeInfo.GetUnicodeCategory(char.ConvertFromUtf32((int)unicode), 0);
        return category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;
    }

    private sealed class TextLine
    {
        public TextLine(IEnumerable<PdfTextCharacter> characters, PdfRect bounds)
        {
            Characters = characters.OrderBy(character => character.LooseBounds.CenterX).ToArray();
            Bounds = bounds;
        }

        public PdfTextCharacter[] Characters { get; }
        public PdfRect Bounds { get; }

        public int FindClosestCharacter(double x)
        {
            int low = 0;
            int high = Characters.Length - 1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2;
                if (Characters[middle].LooseBounds.CenterX < x) low = middle + 1;
                else high = middle - 1;
            }
            return Math.Clamp(low, 0, Characters.Length - 1);
        }
    }
}
