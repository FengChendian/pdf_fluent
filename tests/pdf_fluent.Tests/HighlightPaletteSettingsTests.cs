using Avalonia.Media;
using pdf_fluent.Models;
using pdf_fluent.Services;
using Xunit;

namespace pdf_fluent.Tests;

public sealed class HighlightPaletteSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"pdf-palette-{Guid.NewGuid():N}");
    private string StoragePath => Path.Combine(_directory, "palette.json");

    [Fact]
    public void PresetSelectionPersistsWithoutOverwritingTheCustomColor()
    {
        var settings = new HighlightPaletteSettings(StoragePath);
        settings.SelectColor(Color.Parse("#408764AE"), custom: true);
        settings.SelectColor(HighlightPalette.Blue, custom: false);

        var reloaded = new HighlightPaletteSettings(StoragePath);
        Assert.Equal(HighlightPalette.Blue, reloaded.HighlightColor);
        Assert.Equal(Color.Parse("#408764AE"), reloaded.CustomHighlightColor);
        Assert.False(reloaded.IsCustomHighlightColor);
    }

    [Fact]
    public void CustomSelectionMatchingAPresetStillPersistsAsCustom()
    {
        var settings = new HighlightPaletteSettings(StoragePath);
        settings.SelectColor(HighlightPalette.Yellow, custom: true);

        var reloaded = new HighlightPaletteSettings(StoragePath);
        Assert.True(reloaded.IsCustomHighlightColor);
        Assert.Equal(HighlightPalette.Yellow, reloaded.HighlightColor);
    }

    [Theory]
    [InlineData("invalid json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"HighlightColor\":\"invalid\",\"CustomHighlightColor\":\"invalid\"}")]
    public void InvalidSettingsFallBackToDefaults(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(StoragePath, json);
        var settings = new HighlightPaletteSettings(StoragePath);
        Assert.Equal(HighlightPalette.Yellow, settings.HighlightColor);
        Assert.Equal(HighlightPalette.Yellow, settings.CustomHighlightColor);
        settings.SelectColor(HighlightPalette.Green, custom: false);
        Assert.Equal(HighlightPalette.Green, new HighlightPaletteSettings(StoragePath).HighlightColor);
    }

    [Fact]
    public void UnwritableSettingsStillAllowColorSelection()
    {
        Directory.CreateDirectory(StoragePath);
        var settings = new HighlightPaletteSettings(StoragePath);
        settings.SelectColor(HighlightPalette.Green, custom: true);
        Assert.Equal(HighlightPalette.Green, settings.HighlightColor);
        Assert.Equal(HighlightPalette.Green, settings.CustomHighlightColor);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
