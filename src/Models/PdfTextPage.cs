using System.Globalization;
using System.Text;

namespace pdf_fluent.Models;

public sealed class PdfTextPage
{
    // PDF points. Chromium's PDF viewer uses the same 20pt hit-test radius
    // (FPDFText_GetCharIndexAtPos in pdf/pdfium/pdfium_page.cc), which is what
    // makes Edge pick the column on the pointer's side of a gutter.
    private const double ColumnProbeRadius = 20;

    private readonly int[] _textOffsets;
    private readonly TextLine[] _lines;
    private readonly double _minColumnGapWidth;
    private readonly double _minColumnBandWidth;

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

        double medianGlyphHeight = MeasureMedianGlyphHeight(Characters, CropBox);
        _minColumnGapWidth = Math.Max(3, medianGlyphHeight * 0.5);
        _minColumnBandWidth = Math.Max(6, medianGlyphHeight * 0.8);
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

        if (!nearestOnLine)
        {
            return FindHit(
                x, y, nearestOnLine: false, xTolerance, yTolerance,
                columnBands: null, scanAllCharacters: false,
                skipWhitespace: false, requireOnText: true);
        }

        // While dragging, a pointer in a column gutter must resolve to the
        // column on its own side of the gutter's centre line, not to whichever
        // character happens to be closest. Edge behaves the same way because
        // PDFium looks for a character within a radius (20pt) around the
        // pointer and then picks the caret side from the character box.
        //
        // A gutter is detected from the visible-glyph gaps of the lines near
        // the pointer: every line that participates must leave the same
        // horizontal band empty. Using several lines keeps a paragraph's short
        // last line (whose local gap is much wider than the real gutter) from
        // moving the centre line to the left.
        ColumnBand[] columnBands = FindColumnBands(y);
        if (columnBands.Length == 0)
        {
            return FindHit(
                x, y, nearestOnLine: true, xTolerance, yTolerance,
                columnBands: null, scanAllCharacters: false,
                skipWhitespace: false, requireOnText: false);
        }

        // Text under the pointer always wins, even when the line spans the
        // gutter (a title or a figure caption). Only blank paper is resolved
        // against the gutter boundary.
        PdfTextHit? onText = FindHit(
            x, y, nearestOnLine: true, xTolerance, yTolerance,
            columnBands: null, scanAllCharacters: true,
            skipWhitespace: false, requireOnText: true);
        if (onText is not null) return onText;

        bool pointerIsInGutter = false;
        foreach (ColumnBand band in columnBands)
        {
            if (x >= band.Left && x <= band.Right)
            {
                pointerIsInGutter = true;
                break;
            }
        }

        return FindHit(
            x, y, nearestOnLine: true, xTolerance, yTolerance,
            columnBands, scanAllCharacters: true,
            skipWhitespace: pointerIsInGutter, requireOnText: false);
    }

    private PdfTextHit? FindHit(
        double x,
        double y,
        bool nearestOnLine,
        double xTolerance,
        double yTolerance,
        ColumnBand[]? columnBands,
        bool scanAllCharacters,
        bool skipWhitespace,
        bool requireOnText)
    {
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

            if (scanAllCharacters)
            {
                // A gutter can be much wider than the characters adjacent to
                // the caret (a short last line, for example), so inspect the
                // whole line instead of only the nearest few characters.
                foreach (PdfTextCharacter character in line.Characters)
                {
                    // While the pointer is inside the gutter, invisible space
                    // characters must not become the caret target: a leading
                    // space in the right column can overlap the gutter and
                    // would otherwise win before the pointer reaches the
                    // visible right-column glyph.
                    if (skipWhitespace && IsLayoutWhitespace(character.Unicode)) continue;
                    if (columnBands is not null &&
                        IsExcludedByColumnBands(character, x, columnBands))
                    {
                        continue;
                    }
                    Consider(character);
                }
            }
            else
            {
                int closestCharacter = line.FindClosestCharacter(x);
                for (int i = Math.Max(0, closestCharacter - 2);
                     i <= Math.Min(line.Characters.Length - 1, closestCharacter + 2);
                     i++)
                {
                    Consider(line.Characters[i]);
                }
            }
        }

        if (bestCharacter < 0) return null;
        PdfTextCharacter selected = Characters[bestCharacter];
        int caret = x < selected.LooseBounds.CenterX ? bestCharacter : bestCharacter + 1;
        return new PdfTextHit(bestCharacter, caret, bestIsOnText);

        void Consider(PdfTextCharacter character)
        {
            PdfRect bounds = character.LooseBounds;
            bool isOnText = bounds.Contains(x, y, xTolerance, nearestOnLine ? yTolerance : 0);
            if (requireOnText && !isOnText) return;

            double dx = Math.Max(Math.Max(bounds.L - x, x - bounds.R), 0);
            double dy = Math.Max(Math.Max(bounds.B - y, y - bounds.T), 0);
            double distance = dx * dx + dy * dy;
            if (distance >= bestDistance) return;
            bestDistance = distance;
            bestCharacter = character.Index;
            bestIsOnText = isOnText;
        }
    }

    /// <summary>
    /// Finds the horizontal bands that are empty on at least two lines near
    /// <paramref name="y"/>. Only visible glyphs take part, so leading or
    /// trailing space characters cannot move a column boundary.
    /// </summary>
    private ColumnBand[] FindColumnBands(double y)
    {
        int closestLine = FindClosestLine(y);
        var probeGaps = new List<ProbeGap>();
        int first = Math.Max(0, closestLine - 4);
        int last = Math.Min(_lines.Length - 1, closestLine + 4);
        for (int lineIndex = first; lineIndex <= last; lineIndex++)
        {
            TextLine line = _lines[lineIndex];
            double verticalDistance = y < line.Bounds.B
                ? line.Bounds.B - y
                : y > line.Bounds.T ? y - line.Bounds.T : 0;
            if (verticalDistance > ColumnProbeRadius) continue;
            foreach (ColumnGap gap in line.ColumnGaps)
                probeGaps.Add(new ProbeGap(gap.Left, gap.Right, lineIndex));
        }
        if (probeGaps.Count < 2) return [];

        var events = new List<GapEvent>(probeGaps.Count * 2);
        foreach (ProbeGap gap in probeGaps)
        {
            events.Add(new GapEvent(gap.Left, 1, gap.LineIndex));
            events.Add(new GapEvent(gap.Right, -1, gap.LineIndex));
        }
        events.Sort(static (left, right) =>
            left.X != right.X ? left.X.CompareTo(right.X) : left.Delta.CompareTo(right.Delta));

        var activeLines = new HashSet<int>();
        var bands = new List<ColumnBand>();
        bool hasBand = false;
        double bandLeft = 0;
        double bandRight = 0;
        int index = 0;
        while (index < events.Count)
        {
            double eventX = events[index].X;
            while (index < events.Count && events[index].X == eventX)
            {
                GapEvent gapEvent = events[index++];
                if (gapEvent.Delta < 0) activeLines.Remove(gapEvent.LineIndex);
                else activeLines.Add(gapEvent.LineIndex);
            }

            if (index >= events.Count) break;
            double nextX = events[index].X;
            if (nextX <= eventX) continue;

            if (activeLines.Count >= 2)
            {
                if (!hasBand)
                {
                    bandLeft = eventX;
                    hasBand = true;
                }
                bandRight = nextX;
            }
            else if (hasBand)
            {
                if (bandRight - bandLeft >= _minColumnBandWidth)
                    bands.Add(new ColumnBand(bandLeft, bandRight));
                hasBand = false;
            }
        }
        if (hasBand && bandRight - bandLeft >= _minColumnBandWidth)
            bands.Add(new ColumnBand(bandLeft, bandRight));
        return bands.ToArray();
    }

    /// <summary>
    /// Rejects a character that lies on the far side of a detected gutter's
    /// centre line. Characters whose ink crosses the centre line are kept, so
    /// text spanning both columns (a title, a figure caption, a table row) is
    /// not excluded; on-text hits are resolved before this filter runs.
    /// </summary>
    private static bool IsExcludedByColumnBands(
        PdfTextCharacter character,
        double x,
        ColumnBand[] bands)
    {
        PdfRect ink = InkBounds(character);
        foreach (ColumnBand band in bands)
        {
            if (x <= band.Midline)
            {
                if (ink.L >= band.Midline) return true;
            }
            else if (ink.R <= band.Midline)
            {
                return true;
            }
        }
        return false;
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
        return MergeOverlappingRects(rects);
    }

    private static IReadOnlyList<PdfRect> MergeOverlappingRects(IReadOnlyList<PdfRect> rects)
    {
        var merged = new List<PdfRect>(rects);
        bool changed;
        do
        {
            changed = false;
            for (int first = 0; first < merged.Count && !changed; first++)
            {
                for (int second = first + 1; second < merged.Count; second++)
                {
                    PdfRect left = merged[first];
                    PdfRect right = merged[second];
                    double verticalOverlap = Math.Min(left.T, right.T) - Math.Max(left.B, right.B);
                    bool sameVisualLine = verticalOverlap >= Math.Min(left.Height, right.Height) * 0.35;
                    bool horizontalOverlap = right.L <= left.R + 0.5 && left.L <= right.R + 0.5;
                    if (!sameVisualLine || !horizontalOverlap) continue;

                    merged[first] = left.Union(right);
                    merged.RemoveAt(second);
                    changed = true;
                    break;
                }
            }
        } while (changed);

        return merged;
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

            if (current is not null)
                lines.Add(new TextLine(current, currentBounds, CropBox, _minColumnGapWidth));
            current = [character];
            currentBounds = bounds;
        }

        if (current is not null)
            lines.Add(new TextLine(current, currentBounds, CropBox, _minColumnGapWidth));
        return lines.OrderByDescending(line => line.Bounds.CenterY).ToArray();
    }

    private static double MeasureMedianGlyphHeight(
        IReadOnlyList<PdfTextCharacter> characters,
        PdfRect cropBox)
    {
        var heights = new List<double>();
        foreach (PdfTextCharacter character in characters)
        {
            if (IsLayoutWhitespace(character.Unicode)) continue;
            PdfRect ink = InkBounds(character);
            if (ink.Width <= 0 || ink.Height <= 0 || !ink.Intersects(cropBox)) continue;
            heights.Add(ink.Height);
        }
        if (heights.Count == 0) return 0;
        heights.Sort();
        return heights[heights.Count / 2];
    }

    private static bool IsVisibleGlyph(PdfTextCharacter character, PdfRect cropBox)
    {
        if (IsLayoutWhitespace(character.Unicode)) return false;
        PdfRect ink = InkBounds(character);
        return ink is { Width: > 0, Height: > 0 } && ink.Intersects(cropBox);
    }

    /// <summary>Tight glyph box, falling back to the loose box when PDFium has no glyph box.</summary>
    private static PdfRect InkBounds(PdfTextCharacter character) =>
        character.Bounds is { Width: > 0, Height: > 0 } ? character.Bounds : character.LooseBounds;

    private static bool IsLayoutWhitespace(uint unicode)
    {
        if (unicode == 0 || unicode > 0x10FFFF) return true;
        if (unicode <= char.MaxValue) return char.IsWhiteSpace((char)unicode);
        UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(
            char.ConvertFromUtf32((int)unicode), 0);
        return category is UnicodeCategory.SpaceSeparator
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator;
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

    private readonly record struct ColumnGap(double Left, double Right);

    private readonly record struct ColumnBand(double Left, double Right)
    {
        public double Midline => (Left + Right) / 2;
    }

    private readonly record struct ProbeGap(double Left, double Right, int LineIndex);

    private readonly record struct GapEvent(double X, int Delta, int LineIndex);

    private sealed class TextLine
    {
        public TextLine(
            IEnumerable<PdfTextCharacter> characters,
            PdfRect bounds,
            PdfRect cropBox,
            double minColumnGapWidth)
        {
            Characters = characters.OrderBy(character => character.LooseBounds.CenterX).ToArray();
            Bounds = bounds;
            PdfTextCharacter[] visibleGlyphs = Characters
                .Where(character => IsVisibleGlyph(character, cropBox))
                .OrderBy(character => InkBounds(character).L)
                .ToArray();
            ColumnGaps = BuildColumnGaps(visibleGlyphs, minColumnGapWidth);
        }

        public PdfTextCharacter[] Characters { get; }
        public PdfRect Bounds { get; }
        public ColumnGap[] ColumnGaps { get; }

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

        private static ColumnGap[] BuildColumnGaps(
            PdfTextCharacter[] visibleGlyphs,
            double minColumnGapWidth)
        {
            var gaps = new List<ColumnGap>(visibleGlyphs.Length);
            for (int i = 1; i < visibleGlyphs.Length; i++)
            {
                double left = InkBounds(visibleGlyphs[i - 1]).R;
                double right = InkBounds(visibleGlyphs[i]).L;
                if (right - left >= minColumnGapWidth)
                    gaps.Add(new ColumnGap(left, right));
            }
            return gaps.ToArray();
        }
    }
}
