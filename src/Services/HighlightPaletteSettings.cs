using System.Text.Json;
using Avalonia.Media;
using pdf_fluent.Models;

namespace pdf_fluent.Services;

/// <summary>Application-wide pen preferences, independent of document lifetimes.</summary>
public sealed class HighlightPaletteSettings
{
    private readonly string? _storagePath;

    public HighlightPaletteSettings() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "pdf_fluent", "highlight-palette.json"))
    {
    }

    // A null path provides isolated, in-memory preferences for standalone readers/tests.
    internal HighlightPaletteSettings(string? storagePath)
    {
        _storagePath = storagePath;
        Load();
    }

    public Color HighlightColor { get; private set; } = HighlightPalette.Yellow;
    public Color CustomHighlightColor { get; private set; } = HighlightPalette.Yellow;
    public bool IsCustomHighlightColor { get; private set; }
    public event EventHandler? Changed;

    public void SelectColor(Color color, bool custom)
    {
        if (HighlightColor == color && IsCustomHighlightColor == custom &&
            (!custom || CustomHighlightColor == color)) return;
        HighlightColor = color;
        IsCustomHighlightColor = custom;
        if (custom) CustomHighlightColor = color;
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Load()
    {
        if (_storagePath is null) return;
        try
        {
            if (!File.Exists(_storagePath)) return;
            var data = JsonSerializer.Deserialize<PaletteData>(File.ReadAllText(_storagePath));
            if (data is null) return;
            if (Color.TryParse(data.CustomHighlightColor, out var custom)) CustomHighlightColor = custom;
            if (Color.TryParse(data.HighlightColor, out var selected)) HighlightColor = selected;
            IsCustomHighlightColor = data.IsCustomHighlightColor;
            if (IsCustomHighlightColor) HighlightColor = CustomHighlightColor;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
    }

    private void Save()
    {
        if (_storagePath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storagePath)!);
            string json = JsonSerializer.Serialize(new PaletteData(
                $"#{HighlightColor.ToUInt32():X8}", $"#{CustomHighlightColor.ToUInt32():X8}", IsCustomHighlightColor));
            // Replace the complete file so interrupted writes leave the previous preferences intact.
            string temporaryPath = _storagePath + ".tmp";
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _storagePath, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record PaletteData(string? HighlightColor, string? CustomHighlightColor, bool IsCustomHighlightColor);
}
