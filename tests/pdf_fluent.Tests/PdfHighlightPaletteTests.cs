using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using pdf_fluent.Controls;
using pdf_fluent.Models;
using pdf_fluent.Services;
using pdf_fluent.ViewModels;
using pdf_fluent.Views;
using Xunit;
using static pdf_fluent.Tests.PdfTestHelpers;

namespace pdf_fluent.Tests;

public sealed class PdfHighlightPaletteTests
{
    [AvaloniaTheory]
    [InlineData("#FFFF9EA9")]
    [InlineData("#FFFFBE81")]
    [InlineData("#FFFFEB3B")]
    [InlineData("#FFB3FFCD")]
    [InlineData("#FF9FC5FF")]
    [InlineData("#FF000000")]
    [InlineData("#FFFFFFFF")]
    [InlineData("#FF777777")]
    [InlineData("#FF0000FF")]
    [InlineData("#408764AE")]
    [InlineData("#00000000")]
    public async Task SelectedPenUsesTheHighlightColorDuringHoverAndPress(string hex)
    {
        using var reader = await Reader.Create();
        reader.Document.HighlightColor = Color.Parse(hex);
        Click(reader.Window, reader.View.HighlightButton);
        reader.Window.UpdateLayout();
        AssertSelectedPenColors(reader);
        reader.Window.MouseMove(new Point(20, 400));
        AssertSelectedPenColors(reader);
        reader.Window.MouseMove(Center(reader.View.HighlightButton, reader.Window));
        AssertSelectedPenColors(reader);
        reader.Window.MouseDown(Center(reader.View.HighlightButton, reader.Window), MouseButton.Left);
        AssertSelectedPenColors(reader);
        reader.Window.MouseUp(Center(reader.View.HighlightButton, reader.Window), MouseButton.Left);
        reader.Window.MouseMove(new Point(20, 400));
        Assert.False(reader.Document.IsHighlightMode);
        var surface = Named<ContentPresenter>(reader.View.HighlightButton, "PART_ContentPresenter");
        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(surface.Background).Color);
        Assert.Equal(Color.Parse("#666D80"), Assert.IsAssignableFrom<ISolidColorBrush>(
            Named<Optris.Icons.Avalonia.Icon>(reader.View, "HighlightToolIcon").Foreground).Color);
    }

    [AvaloniaTheory]
    [InlineData(900)]
    [InlineData(1200)]
    public async Task PaletteHasSixSwatchesAndACurvedPointerAnchoredToTheAlignedPen(int width)
    {
        using var reader = await Reader.Create(width);
        var view = reader.View;
        var window = reader.Window;
        Rect viewport = Named<Grid>(view, "DocumentViewport").Bounds;
        AssertToolbarIconAlignment(view);
        Click(window, view.HighlightButton);
        window.UpdateLayout();
        await Task.Delay(260);
        window.UpdateLayout();
        var overlay = Named<Grid>(view, "HighlightPaletteOverlay");
        var panel = Named<Border>(view, "HighlightPalettePanel");
        var pointer = Named<Path>(view, "HighlightPalettePointer");
        var swatches = Named<StackPanel>(view, "HighlightSwatches").Children.OfType<Button>().ToArray();
        Assert.True(overlay.IsVisible);
        Assert.True(reader.Document.IsHighlightMode);
        Assert.Equal(6, swatches.Length);
        Assert.All(swatches, swatch => Assert.Equal(swatches[0].Bounds.Y, swatch.Bounds.Y));
        Assert.Equal(new CornerRadius(12), panel.CornerRadius);
        Assert.Equal(Colors.White, Assert.IsAssignableFrom<ISolidColorBrush>(panel.Background).Color);
        Assert.Equal(Color.Parse("#DDE2EC"), Assert.IsAssignableFrom<ISolidColorBrush>(panel.BorderBrush).Color);
        // Layout rounding can place an even-width pointer half a DIP off an odd-width pen.
        Assert.InRange(Math.Abs(Center(view.HighlightButton, view).X - Center(pointer, view).X), 0, 0.501);
        Assert.Equal(Named<Border>(view, "ReaderHeader").Bounds.Bottom, panel.TranslatePoint(default, view)!.Value.Y, 1);
        Assert.Equal(viewport, Named<Grid>(view, "DocumentViewport").Bounds);
        AssertToolbarIconAlignment(view);
        Assert.Empty(overlay.GetVisualDescendants().OfType<Slider>());
        Assert.InRange(overlay.Bounds.Height, 1, 72);
        Assert.True(overlay.TranslatePoint(new Point(overlay.Bounds.Width, 0), view)!.Value.X <= view.Bounds.Width);
        Assert.DoesNotContain(Named<Button>(view, "HighlightCustomSwatch").GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "+");

        window.MouseMove(Center(swatches[0], window));
        var presenter = Named<ContentPresenter>(swatches[0], "PART_ContentPresenter");
        Assert.Equal(Color.Parse("#F0F2F8"), Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background).Color);
        Assert.Contains("selected", Named<Button>(view, "HighlightYellowSwatch").Classes);
        var selectedSurface = Named<ContentPresenter>(Named<Button>(view, "HighlightYellowSwatch"), "PART_ContentPresenter");
        Assert.Equal(new CornerRadius(8), selectedSurface.CornerRadius);
        Assert.Equal(Color.Parse("#8B7ED8"), Assert.IsAssignableFrom<ISolidColorBrush>(selectedSurface.BorderBrush).Color);
    }

    [AvaloniaFact]
    public async Task PaletteFadesAndSlidesInOnEveryOpenWithoutMovingTheDocument()
    {
        using var reader = await Reader.Create();
        var view = reader.View;
        var window = reader.Window;
        var overlay = Named<Grid>(view, "HighlightPaletteOverlay");
        Rect viewport = Named<Grid>(view, "DocumentViewport").Bounds;
        Assert.Equal(0, overlay.Opacity);
        Assert.Equal(32, overlay.Margin.Top);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            Click(window, view.HighlightButton);
            window.UpdateLayout();
            Assert.True(overlay.IsVisible);
            Assert.True(overlay.Opacity < 1);
            Assert.InRange(overlay.Margin.Top, 32, 39.99);
            await Task.Delay(260);
            window.UpdateLayout();
            Assert.Equal(1, overlay.Opacity);
            Assert.Equal(40, overlay.Margin.Top);
            Assert.Equal(viewport, Named<Grid>(view, "DocumentViewport").Bounds);
            Assert.InRange(Math.Abs(Center(view.HighlightButton, view).X -
                                   Center(Named<Path>(view, "HighlightPalettePointer"), view).X), 0, 0.501);
            Click(window, view.HighlightButton);
            Assert.False(overlay.IsVisible);
            Assert.False(reader.Document.IsHighlightMode);
            await Task.Delay(260);
            window.UpdateLayout();
            Assert.Equal(0, overlay.Opacity);
            Assert.Equal(32, overlay.Margin.Top);
        }
    }

    [AvaloniaFact]
    public async Task CustomSwatchStaysAnOpaqueFullSaturationRainbowAfterEditingColors()
    {
        using var reader = await Reader.Create();
        var view = reader.View;
        Click(reader.Window, view.HighlightButton);
        reader.Window.UpdateLayout();
        var swatch = Named<Button>(view, "HighlightCustomSwatch");
        var rainbow = Assert.IsType<ConicGradientBrush>(Named<Border>(view, "HighlightCustomSwatchRainbow").Background);
        Assert.Equal(1, rainbow.Opacity);
        Assert.Equal(7, rainbow.GradientStops.Count);
        foreach (GradientStop stop in rainbow.GradientStops)
        {
            Assert.Equal(255, stop.Color.A);
            Assert.Equal(1, stop.Color.ToHsv().S);
            Assert.Equal(1, stop.Color.ToHsv().V);
        }
        Click(reader.Window, swatch);
        Named<TextBox>(view, "HighlightArgbInput").Text = "#10202020";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Color.Parse("#10202020"), reader.Document.HighlightColor);
        Assert.Same(rainbow, Named<Border>(view, "HighlightCustomSwatchRainbow").Background);
    }

    [AvaloniaFact]
    public async Task AllCustomColorInputsCenterTheirActualTextVertically()
    {
        using var reader = await Reader.Create();
        var view = reader.View;
        Click(reader.Window, view.HighlightButton);
        reader.Window.UpdateLayout();
        Click(reader.Window, Named<Button>(view, "HighlightCustomSwatch"));
        await Task.Delay(260);
        reader.Window.UpdateLayout();
        foreach (string name in new[] { "HighlightRedInput", "HighlightGreenInput", "HighlightBlueInput", "HighlightArgbInput" })
        {
            var input = Named<Control>(view, name);
            var textBox = input as TextBox ?? input.GetVisualDescendants().OfType<TextBox>().Single();
            Assert.Equal(VerticalAlignment.Center, textBox.VerticalContentAlignment);
            var text = textBox.GetVisualDescendants().OfType<TextPresenter>().Single();
            Assert.InRange(Math.Abs(Center(input, view).Y - Center(text, view).Y), 0, 0.501);
        }
    }

    [AvaloniaTheory]
    [InlineData("HighlightCustomSwatchPreview")]
    [InlineData("HighlightCustomSwatchRainbow")]
    [InlineData("HighlightCustomSwatch")]
    public async Task CustomCapsuleIsOneClickableButtonWithALivePreview(string clickTarget)
    {
        using var reader = await Reader.Create();
        var view = reader.View;
        var window = reader.Window;
        Click(window, view.HighlightButton);
        await Task.Delay(260);
        window.UpdateLayout();
        var capsule = Named<Button>(view, "HighlightCustomSwatch");
        var preview = Named<Border>(view, "HighlightCustomSwatchPreview");
        var rainbow = Named<Border>(view, "HighlightCustomSwatchRainbow");
        var surface = Named<ContentPresenter>(capsule, "PART_ContentPresenter");
        var panel = Named<StackPanel>(view, "HighlightCustomPanel");
        Assert.Empty(capsule.GetVisualDescendants().OfType<Button>());
        Assert.True(capsule.Bounds.Width > capsule.Bounds.Height);
        Assert.Equal(capsule.Bounds.Height / 2, surface.CornerRadius.TopLeft);
        Assert.True(Center(preview, view).X < Center(rainbow, view).X);
        Assert.Equal(Center(preview, view).Y, Center(rainbow, view).Y);
        Assert.Equal(reader.Document.HighlightColor, Assert.IsAssignableFrom<ISolidColorBrush>(preview.Background).Color);
        Assert.Equal(Color.Parse("#DDE2EC"), Assert.IsAssignableFrom<ISolidColorBrush>(surface.BorderBrush).Color);

        var target = Named<Control>(view, clickTarget);
        window.MouseMove(Center(target, window));
        Assert.True(capsule.IsPointerOver);
        Assert.Equal(Color.Parse("#F0F2F8"), Assert.IsAssignableFrom<ISolidColorBrush>(surface.Background).Color);
        Click(window, target);
        window.UpdateLayout();
        Assert.True(panel.IsVisible);
        Assert.Equal(new CornerRadius(16), surface.CornerRadius);
        Assert.Equal(Color.Parse("#8B7ED8"), Assert.IsAssignableFrom<ISolidColorBrush>(surface.BorderBrush).Color);

        Named<TextBox>(view, "HighlightArgbInput").Text = "#408764AE";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Color.Parse("#408764AE"), Assert.IsAssignableFrom<ISolidColorBrush>(preview.Background).Color);
        Named<NumericUpDown>(view, "HighlightRedInput").Value = 160;
        Assert.Equal(Color.Parse("#40A064AE"), Assert.IsAssignableFrom<ISolidColorBrush>(preview.Background).Color);
        Color custom = reader.Document.HighlightColor;
        Click(window, Named<Button>(view, "HighlightCustomCollapseButton"));
        Assert.False(panel.IsVisible);
        Click(window, Named<Button>(view, "HighlightBlueSwatch"));
        Assert.Equal(custom, Assert.IsAssignableFrom<ISolidColorBrush>(preview.Background).Color);
        Assert.Equal(Color.Parse("#DDE2EC"), Assert.IsAssignableFrom<ISolidColorBrush>(surface.BorderBrush).Color);
        Click(window, target);
        Assert.True(panel.IsVisible);
        Assert.Equal(custom, Assert.IsAssignableFrom<ISolidColorBrush>(preview.Background).Color);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PresetsAndPaletteReopeningPreserveTheCustomSwatchColor(bool editCustomColor)
    {
        using var reader = await Reader.Create();
        var view = reader.View;
        var window = reader.Window;
        Click(window, view.HighlightButton);
        window.UpdateLayout();
        var preview = Named<Border>(view, "HighlightCustomSwatchPreview");
        Color custom = HighlightPalette.Yellow;
        if (editCustomColor)
        {
            Click(window, Named<Button>(view, "HighlightCustomSwatch"));
            Named<TextBox>(view, "HighlightArgbInput").Text = "#408764AE";
            Dispatcher.UIThread.RunJobs();
            custom = Color.Parse("#408764AE");
        }

        foreach (string name in new[] { "Red", "Orange", "Yellow", "Green", "Blue" })
        {
            var preset = Named<Button>(view, $"Highlight{name}Swatch");
            Click(window, preset);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Assert.IsType<Color>(preset.Tag), reader.Document.HighlightColor);
            Assert.Equal(custom, Assert.IsAssignableFrom<ISolidColorBrush>(preview.Background).Color);
            Click(window, view.HighlightButton);
            Click(window, view.HighlightButton);
            window.UpdateLayout();
            Assert.Equal(custom, Assert.IsAssignableFrom<ISolidColorBrush>(preview.Background).Color);
        }

        Click(window, Named<Button>(view, "HighlightCustomSwatch"));
        Assert.Equal(custom, reader.Document.HighlightColor);
        Assert.Equal(custom, Assert.IsAssignableFrom<ISolidColorBrush>(Named<Border>(view, "HighlightCustomPreview").Background).Color);
    }

    [AvaloniaFact]
    public async Task SwitchingDocumentsSharesTheCustomSwatchAndSelectedColor()
    {
        using var reader = await Reader.Create();
        var view = reader.View;
        Click(reader.Window, view.HighlightButton);
        reader.Window.UpdateLayout();
        Click(reader.Window, Named<Button>(view, "HighlightCustomSwatch"));
        Named<TextBox>(view, "HighlightArgbInput").Text = "#408764AE";
        Dispatcher.UIThread.RunJobs();
        Color custom = reader.Document.HighlightColor;
        Click(reader.Window, Named<Button>(view, "HighlightRedSwatch"));

        using var service = new FakePdfDocumentService(1);
        using var other = new DocumentTabViewModel("other.pdf", service, highlightPaletteSettings: reader.PaletteSettings);
        await other.LoadAsync();
        view.DataContext = other;
        Click(reader.Window, view.HighlightButton);
        reader.Window.UpdateLayout();
        var preview = Named<Border>(view, "HighlightCustomSwatchPreview");
        Assert.Equal(custom, Assert.IsAssignableFrom<ISolidColorBrush>(preview.Background).Color);
        Assert.Equal(HighlightPalette.Red, other.HighlightColor);
        AssertSelectedPenColors(reader);
        Click(reader.Window, Named<Button>(view, "HighlightBlueSwatch"));
        Assert.Equal(custom, Assert.IsAssignableFrom<ISolidColorBrush>(preview.Background).Color);

        view.DataContext = reader.Document;
        Click(reader.Window, view.HighlightButton);
        reader.Window.UpdateLayout();
        Assert.Equal(HighlightPalette.Blue, reader.Document.HighlightColor);
        AssertSelectedPenColors(reader);
        Assert.Equal(custom, Assert.IsAssignableFrom<ISolidColorBrush>(preview.Background).Color);
        Click(reader.Window, Named<Button>(view, "HighlightCustomSwatch"));
        Assert.Equal(custom, reader.Document.HighlightColor);
    }

    [AvaloniaFact]
    public async Task OpenReadersSynchronizeCustomInputsAndSelectionImmediately()
    {
        var settings = new HighlightPaletteSettings(null);
        using var first = await Reader.Create(paletteSettings: settings);
        using var second = await Reader.Create(paletteSettings: settings);
        Click(first.Window, first.View.HighlightButton);
        Click(second.Window, second.View.HighlightButton);
        first.Window.UpdateLayout();
        second.Window.UpdateLayout();
        Click(first.Window, Named<Button>(first.View, "HighlightCustomSwatch"));
        Named<TextBox>(first.View, "HighlightArgbInput").Text = "#408764AE";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("#408764AE", Named<TextBox>(second.View, "HighlightArgbInput").Text);
        Assert.Equal(Color.Parse("#408764AE"), second.Document.CustomHighlightColor);
        Assert.Equal(Color.Parse("#408764AE"), Assert.IsAssignableFrom<ISolidColorBrush>(
            Named<Border>(second.View, "HighlightCustomSwatchPreview").Background).Color);
        Assert.Contains("selected", Named<Button>(second.View, "HighlightCustomSwatch").Classes);
        AssertSelectedPenColors(first);
        AssertSelectedPenColors(second);

        Click(second.Window, Named<Button>(second.View, "HighlightBlueSwatch"));
        Assert.Contains("selected", Named<Button>(first.View, "HighlightBlueSwatch").Classes);
        Assert.Equal(Color.Parse("#408764AE"), first.Document.CustomHighlightColor);
        AssertSelectedPenColors(first);
        AssertSelectedPenColors(second);
    }

    [AvaloniaFact]
    public async Task ClosingAndReopeningReadersRestoresPersistedCustomColorAndSelection()
    {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pdf-palette-{Guid.NewGuid():N}");
        string path = System.IO.Path.Combine(directory, "palette.json");
        try
        {
            using (var reader = await Reader.Create(paletteSettings: new HighlightPaletteSettings(path)))
            {
                Click(reader.Window, reader.View.HighlightButton);
                reader.Window.UpdateLayout();
                Click(reader.Window, Named<Button>(reader.View, "HighlightCustomSwatch"));
                Named<TextBox>(reader.View, "HighlightArgbInput").Text = "#408764AE";
                Dispatcher.UIThread.RunJobs();
            }

            // A fresh settings instance simulates restarting the application.
            using var reopened = await Reader.Create(paletteSettings: new HighlightPaletteSettings(path));
            Click(reopened.Window, reopened.View.HighlightButton);
            reopened.Window.UpdateLayout();
            Assert.Equal(Color.Parse("#408764AE"), reopened.Document.HighlightColor);
            Assert.Equal("#408764AE", Named<TextBox>(reopened.View, "HighlightArgbInput").Text);
            Assert.Equal(Color.Parse("#408764AE"), Assert.IsAssignableFrom<ISolidColorBrush>(
                Named<Border>(reopened.View, "HighlightCustomSwatchPreview").Background).Color);
            Assert.Contains("selected", Named<Button>(reopened.View, "HighlightCustomSwatch").Classes);
            AssertSelectedPenColors(reopened);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task PresetsUseFixedArgbEvenAfterChoosingACustomAlphaWithoutChangingExistingHighlights()
    {
        using var reader = await Reader.Create();
        Assert.Equal(HighlightPalette.Yellow, reader.Document.HighlightColor);
        await reader.Document.AddWordHighlightAsync(0, 1);
        var original = Assert.Single(reader.Document.Highlights);
        Click(reader.Window, reader.View.HighlightButton);
        reader.Window.UpdateLayout();
        foreach (string name in new[] { "Red", "Orange", "Yellow", "Green", "Blue" })
        {
            Click(reader.Window, Named<Button>(reader.View, "HighlightCustomSwatch"));
            Named<TextBox>(reader.View, "HighlightArgbInput").Text = "#CC579ABD";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(204, reader.Document.HighlightColor.A);
            var swatch = Named<Button>(reader.View, $"Highlight{name}Swatch");
            Click(reader.Window, swatch);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("selected", swatch.Classes);
            Color preset = Assert.IsType<Color>(swatch.Tag);
            Assert.Equal(preset, reader.Document.HighlightColor);
            AssertSelectedPenColors(reader);
            Assert.Equal(HighlightPalette.Alpha, reader.Document.HighlightColor.A);
            var surface = Named<ContentPresenter>(swatch, "PART_ContentPresenter");
            Assert.Equal(new CornerRadius(8), surface.CornerRadius);
            Assert.Equal(Color.Parse("#8B7ED8"), Assert.IsAssignableFrom<ISolidColorBrush>(surface.BorderBrush).Color);
            var colorDisc = Assert.IsType<Border>(swatch.Content);
            Assert.Equal(preset, Assert.IsAssignableFrom<ISolidColorBrush>(colorDisc.Background).Color);
            Assert.Equal(colorDisc.Bounds.Width, colorDisc.Bounds.Height);
            Assert.True(colorDisc.CornerRadius.TopLeft >= colorDisc.Bounds.Width / 2);
            await reader.Document.AddWordHighlightAsync(0, 1);
            Assert.Equal(reader.Document.HighlightColor, reader.Document.Highlights.Last().Color);
        }
        Assert.Equal(HighlightPalette.Yellow, original.Color);
    }

    [AvaloniaFact]
    public async Task CustomRgbArgbAndSpectrumStayInSyncAndRejectInvalidHex()
    {
        using var reader = await Reader.Create();
        var view = reader.View;
        Click(reader.Window, view.HighlightButton);
        reader.Window.UpdateLayout();
        Click(reader.Window, Named<Button>(view, "HighlightCustomSwatch"));
        reader.Window.UpdateLayout();
        Assert.True(Named<StackPanel>(view, "HighlightCustomPanel").IsVisible);
        var customSurface = Named<ContentPresenter>(Named<Button>(view, "HighlightCustomSwatch"), "PART_ContentPresenter");
        Assert.Equal(new CornerRadius(16), customSurface.CornerRadius);
        var hex = Named<TextBox>(view, "HighlightArgbInput");
        hex.Text = "#80579ABD";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Color.Parse("#80579ABD"), reader.Document.HighlightColor);
        Assert.Equal(87m, Named<NumericUpDown>(view, "HighlightRedInput").Value);
        Assert.Equal(154m, Named<NumericUpDown>(view, "HighlightGreenInput").Value);
        Assert.Equal(189m, Named<NumericUpDown>(view, "HighlightBlueInput").Value);
        Assert.Equal(reader.Document.HighlightColor, Named<HighlightColorSpectrum>(view, "HighlightSpectrum").Color);

        hex.Text = "#XX";
        Dispatcher.UIThread.RunJobs();
        Assert.True(Named<TextBlock>(view, "HighlightArgbError").IsVisible);
        Assert.Equal(Color.Parse("#80579ABD"), reader.Document.HighlightColor);
        hex.Text = "#80579ABD";
        Dispatcher.UIThread.RunJobs();
        Assert.False(Named<TextBlock>(view, "HighlightArgbError").IsVisible);
        Named<NumericUpDown>(view, "HighlightRedInput").Value = 120;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("#80789ABD", hex.Text);
        Assert.False(Named<TextBlock>(view, "HighlightArgbError").IsVisible);

        var spectrum = Named<HighlightColorSpectrum>(view, "HighlightSpectrum");
        Point p = spectrum.TranslatePoint(new Point(spectrum.Bounds.Width / 2, 30), reader.Window)!.Value;
        reader.Window.MouseDown(p, MouseButton.Left);
        reader.Window.MouseUp(p, MouseButton.Left);
        Assert.Equal(128, reader.Document.HighlightColor.A);
        Assert.NotEqual(Color.Parse("#80789ABD"), reader.Document.HighlightColor);
        Assert.Equal(reader.Document.HighlightColor,
            Assert.IsAssignableFrom<ISolidColorBrush>(Named<Border>(view, "HighlightCustomSwatchPreview").Background).Color);
        hex.Text = "#80FF0000";
        Dispatcher.UIThread.RunJobs();
        Point hue = spectrum.TranslatePoint(new Point(spectrum.Bounds.Width / 2, spectrum.Bounds.Height - 10), reader.Window)!.Value;
        reader.Window.MouseDown(hue, MouseButton.Left);
        reader.Window.MouseUp(hue, MouseButton.Left);
        Assert.Equal(0, reader.Document.HighlightColor.R);
        Assert.Equal(255, reader.Document.HighlightColor.G);
        Assert.InRange(reader.Document.HighlightColor.B, (byte)254, (byte)255);
        Assert.Equal(128, reader.Document.HighlightColor.A);
        Color custom = reader.Document.HighlightColor;
        Click(reader.Window, Named<Button>(view, "HighlightRedSwatch"));
        Click(reader.Window, Named<Button>(view, "HighlightCustomSwatch"));
        Assert.Equal(custom, reader.Document.HighlightColor);
        Assert.Equal(custom, Assert.IsAssignableFrom<ISolidColorBrush>(Named<Border>(view, "HighlightCustomPreview").Background).Color);
    }

    [AvaloniaFact]
    public async Task DismissReopenToggleAndEscapePreserveTheExpectedPenState()
    {
        using var reader = await Reader.Create();
        var view = reader.View;
        var window = reader.Window;
        var overlay = Named<Grid>(view, "HighlightPaletteOverlay");
        Click(window, view.HighlightButton);
        window.UpdateLayout();
        window.MouseDown(new Point(20, 400), MouseButton.Left);
        window.MouseUp(new Point(20, 400), MouseButton.Left);
        Assert.False(overlay.IsVisible);
        Assert.True(reader.Document.IsHighlightMode);
        Click(window, view.HighlightButton);
        Assert.True(overlay.IsVisible);
        Assert.True(view.HighlightButton.IsChecked);
        Click(window, view.HighlightButton);
        Assert.False(overlay.IsVisible);
        Assert.False(reader.Document.IsHighlightMode);
        Assert.False(view.HighlightButton.IsChecked);
        Click(window, view.HighlightButton);
        window.UpdateLayout();
        Click(window, Named<Button>(view, "HighlightCustomSwatch"));
        window.UpdateLayout();
        Named<TextBox>(view, "HighlightArgbInput").Focus();
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(overlay.IsVisible);
        Assert.True(reader.Document.IsHighlightMode);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(reader.Document.IsHighlightMode);
        Assert.False(view.HighlightButton.IsChecked);
    }

    [AvaloniaFact]
    public async Task NavigationAndDocumentSwitchCloseThePalette()
    {
        using var reader = await Reader.Create();
        var view = reader.View;
        Click(reader.Window, view.HighlightButton);
        reader.Window.UpdateLayout();
        Click(reader.Window, Named<TextBlock>(view, "PageIndicatorText"));
        Assert.False(Named<Grid>(view, "HighlightPaletteOverlay").IsVisible);
        Assert.True(view.IsPageJumpVisible);
        Click(reader.Window, view.HighlightButton);
        Assert.False(view.IsPageJumpVisible);
        using var service = new FakePdfDocumentService(1);
        using var other = new DocumentTabViewModel("other.pdf", service);
        await other.LoadAsync();
        view.DataContext = other;
        Assert.False(Named<Grid>(view, "HighlightPaletteOverlay").IsVisible);
        Assert.Equal(HighlightPalette.Yellow, other.HighlightColor);
    }

    private static void AssertSelectedPenColors(Reader reader)
    {
        var document = Assert.IsType<DocumentTabViewModel>(reader.View.DataContext);
        Assert.Equal(document.HighlightColor,
            Assert.IsAssignableFrom<ISolidColorBrush>(reader.View.HighlightButton.Background).Color);
        var surface = Named<ContentPresenter>(reader.View.HighlightButton, "PART_ContentPresenter");
        Color background = Assert.IsAssignableFrom<ISolidColorBrush>(surface.Background).Color;
        var icon = Named<Optris.Icons.Avalonia.Icon>(reader.View, "HighlightToolIcon");
        Color foreground = Assert.IsAssignableFrom<ISolidColorBrush>(icon.Foreground).Color;
        Assert.Equal(document.HighlightColor, background);
        Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(document.HighlightButtonForeground).Color, foreground);
        Assert.Equal(foreground, Assert.IsAssignableFrom<ISolidColorBrush>(surface.Foreground).Color);
        Assert.InRange(HighlightButtonColorsTests.ContrastOnWhite(foreground, background), 4.5, 21);
        Assert.Equal(new CornerRadius(8), surface.CornerRadius);
    }

    private static void AssertToolbarIconAlignment(PdfDocumentView view)
    {
        var penIcon = Named<Control>(view, "HighlightToolIcon");
        foreach (string name in new[] { "SearchButton", "AnnotationButton" })
        {
            var button = Named<Button>(view, name);
            var icon = Assert.IsAssignableFrom<Control>(button.Content);
            Assert.InRange(Math.Abs(Center(icon, view).Y - Center(penIcon, view).Y), 0, 0.5);
            Assert.Equal(Center(button, view).Y, Center(view.HighlightButton, view).Y);
        }
    }

    private sealed class Reader : IDisposable
    {
        private readonly FakePdfDocumentService _service = new(1);
        public DocumentTabViewModel Document { get; }
        public PdfDocumentView View { get; }
        public Window Window { get; }
        public HighlightPaletteSettings PaletteSettings { get; }
        private Reader(int width, HighlightPaletteSettings? paletteSettings)
        {
            PaletteSettings = paletteSettings ?? new HighlightPaletteSettings(null);
            Document = new DocumentTabViewModel("unused.pdf", _service, highlightPaletteSettings: PaletteSettings);
            View = new PdfDocumentView { DataContext = Document };
            Window = new Window { Width = width, Height = 900, Content = View };
        }
        public static async Task<Reader> Create(int width = 1200, HighlightPaletteSettings? paletteSettings = null)
        {
            var reader = new Reader(width, paletteSettings);
            await reader.Document.LoadAsync();
            reader.Window.Show();
            reader.Window.UpdateLayout();
            return reader;
        }
        public void Dispose()
        {
            Window.Close();
            Document.Dispose();
            _service.Dispose();
        }
    }
}
