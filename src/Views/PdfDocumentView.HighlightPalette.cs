using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using pdf_fluent.ViewModels;

namespace pdf_fluent.Views;

public partial class PdfDocumentView
{
    private bool _syncingHighlightPalette;

    private IEnumerable<Button> PresetHighlightSwatches =>
        HighlightSwatches.Children.OfType<Button>().Where(button => button.Tag is Color);

    private void InitializeHighlightPalette()
    {
        HighlightPaletteOverlay.Transitions = CreateReaderFlyoutTransitions();
        HighlightSpectrum.ColorChanged += OnHighlightSpectrumChanged;
        AddHandler(PointerPressedEvent, OnHighlightPalettePointerPressed, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnHighlightPaletteKeyDown, RoutingStrategies.Tunnel);
        LayoutUpdated += (_, _) => PositionHighlightPalette();
    }

    private void OnHighlightDocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentTabViewModel.HighlightColor) or
            nameof(DocumentTabViewModel.CustomHighlightColor) or nameof(DocumentTabViewModel.IsCustomHighlightColor))
            SyncHighlightPalette();
        if (e.PropertyName == nameof(DocumentTabViewModel.IsHighlightMode) &&
            DataContext is DocumentTabViewModel { IsHighlightMode: false }) CloseHighlightPalette();
    }

    private void OnHighlightButtonClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DocumentTabViewModel document) return;
        if (HighlightPaletteOverlay.IsVisible)
        {
            document.IsHighlightMode = false;
            CloseHighlightPalette();
            return;
        }

        // Clicking an active pen after dismissing the palette opens its colors again.
        document.IsHighlightMode = true;
        HighlightButton.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        ClosePageJump();
        CloseTableOfContentsImmediately();
        SyncHighlightPalette();
        HighlightPaletteOverlay.IsVisible = true;
        PositionHighlightPalette();
        HighlightPaletteOverlay.Classes.Add("open");
    }

    private void PositionHighlightPalette()
    {
        if (!HighlightPaletteOverlay.IsVisible || HighlightButton.TranslatePoint(default, this) is not { } origin) return;
        double center = origin.X + HighlightButton.Bounds.Width / 2;
        double paletteWidth = HighlightPaletteOverlay.Width;
        double left = Math.Clamp(center - paletteWidth / 2, 8, Math.Max(8, Bounds.Width - paletteWidth - 8));
        // Horizontal anchoring must not replace the animated vertical margin.
        ((TranslateTransform)HighlightPaletteOverlay.RenderTransform!).X = left;
        var pointerMargin = new Thickness(Math.Clamp(center - left - 10, 12, paletteWidth - 32), 0, 0, 0);
        HighlightPalettePointer.Margin = pointerMargin;
        HighlightPalettePointerFill.Margin = pointerMargin;
    }

    private void OnHighlightPalettePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!HighlightPaletteOverlay.IsVisible || e.Source is not Visual source) return;
        if (!IsWithin(source, HighlightPaletteOverlay) && !IsWithin(source, HighlightButton)) CloseHighlightPalette();
    }

    private void OnHighlightPaletteKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !HighlightPaletteOverlay.IsVisible) return;
        CloseHighlightPalette();
        HighlightButton.Focus();
        e.Handled = true;
    }

    private void CloseHighlightPalette()
    {
        HighlightPaletteOverlay.Classes.Remove("open");
        HighlightPaletteOverlay.IsVisible = false;
        HighlightCustomPanel.IsVisible = false;
    }

    private void OnHighlightSwatchClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DocumentTabViewModel document || sender is not Button { Tag: Color preset }) return;
        HighlightCustomPanel.IsVisible = false;
        document.HighlightColor = preset;
        SyncHighlightPalette();
    }

    private void OnHighlightCustomClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DocumentTabViewModel document) return;
        document.SelectHighlightColor(document.CustomHighlightColor, custom: true);
        HighlightCustomPanel.IsVisible = true;
        SyncHighlightPalette();
    }

    private void OnHighlightCustomCloseClick(object? sender, RoutedEventArgs e) => HighlightCustomPanel.IsVisible = false;

    private void OnHighlightSpectrumChanged(object? sender, EventArgs e)
    {
        if (!_syncingHighlightPalette) SetHighlightColor(HighlightSpectrum.Color);
    }

    private void OnHighlightRgbChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_syncingHighlightPalette || DataContext is not DocumentTabViewModel document ||
            HighlightRedInput.Value is not { } red || HighlightGreenInput.Value is not { } green ||
            HighlightBlueInput.Value is not { } blue) return;
        SetHighlightColor(Color.FromArgb(document.HighlightColor.A, (byte)red, (byte)green, (byte)blue));
    }

    private void OnHighlightArgbChanged(object? sender, TextChangedEventArgs e)
    {
        if (_syncingHighlightPalette || HighlightArgbInput is null || DataContext is not DocumentTabViewModel document) return;
        string hex = (HighlightArgbInput.Text ?? "").Trim().TrimStart('#');
        if (hex.Equals($"{document.HighlightColor.ToUInt32():X8}", StringComparison.OrdinalIgnoreCase))
        {
            HighlightArgbError.IsVisible = false;
            return;
        }
        bool valid = hex.Length == 8 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);
        HighlightArgbError.IsVisible = !valid;
        if (!valid) return;
        SetHighlightColor(Color.FromUInt32(uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture)), preserveHex: true);
    }

    private void SetHighlightColor(Color color, bool preserveHex = false)
    {
        if (DataContext is not DocumentTabViewModel document) return;
        // Prevent the model notification from rewriting an ARGB field mid-edit.
        _syncingHighlightPalette = true;
        try
        {
            document.SelectHighlightColor(color, custom: true);
        }
        finally { _syncingHighlightPalette = false; }
        SyncHighlightPalette(preserveHex);
    }

    private void SyncHighlightPalette(bool preserveHex = false)
    {
        if (_syncingHighlightPalette || DataContext is not DocumentTabViewModel document) return;
        _syncingHighlightPalette = true;
        try
        {
            Color color = document.HighlightColor;
            bool presetSelected = false;
            foreach (Button swatch in PresetHighlightSwatches)
            {
                Color preset = (Color)swatch.Tag!;
                bool selected = !document.IsCustomHighlightColor && preset == color;
                swatch.Classes.Set("selected", selected);
                presetSelected |= selected;
            }
            HighlightCustomSwatch.Classes.Set("selected", !presetSelected);
            HighlightSpectrum.Color = color;
            HighlightRedInput.Value = color.R;
            HighlightGreenInput.Value = color.G;
            HighlightBlueInput.Value = color.B;
            if (!preserveHex) HighlightArgbInput.Text = $"#{color.ToUInt32():X8}";
            HighlightArgbError.IsVisible = false;
            HighlightCustomPreview.Background = new SolidColorBrush(color);
            HighlightCustomSwatchPreview.Background = new SolidColorBrush(document.CustomHighlightColor);
        }
        finally { _syncingHighlightPalette = false; }
    }
}
