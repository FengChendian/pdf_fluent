using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using pdf_fluent.ViewModels;
using pdf_fluent.Views;
using Xunit;
using static pdf_fluent.Tests.PdfTestHelpers;

namespace pdf_fluent.Tests;

public sealed class PdfTextInteractionTests
{
    [AvaloniaFact]
    public async Task TextAreaUsesIBeamAndMouseSelectionCopiesExactRange()
    {
        using var service = new FakePdfDocumentService(1);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1200, Height = 900, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            await WaitUntilAsync(() => document.Pages[0].Text is not null);
            window.UpdateLayout();
            Border surface = PageSurface(view, 0);

            Point first = PagePoint(surface, 22, 744, window);
            window.MouseMove(first);
            Assert.True(view.IsTextCursor);
            window.MouseDown(first, MouseButton.Left);
            Point fourth = PagePoint(surface, 60, 744, window);
            window.MouseMove(fourth);
            window.MouseUp(fourth, MouseButton.Left);

            Assert.Equal(0, document.Selection!.Value.Start.CharacterIndex);
            Assert.Equal(4, document.Selection.Value.End.CharacterIndex);
            Assert.Equal("PDF ", await document.GetSelectedTextAsync());
            view.DocumentScroller.Focus();
            Assert.True(view.DocumentScroller.IsFocused);
            window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
            IClipboard clipboard = window.Clipboard!;
            Assert.NotNull(clipboard);
            string? copied = null;
            for (int attempt = 0; attempt < 50 && copied is null; attempt++)
            {
                await Task.Delay(10);
                copied = await clipboard.TryGetTextAsync();
            }
            Assert.Equal("PDF ", copied);

            Point blank = PagePoint(surface, 400, 500, window);
            window.MouseMove(blank);
            Assert.False(view.IsTextCursor);
            window.MouseDown(blank, MouseButton.Left);
            window.MouseUp(blank, MouseButton.Left);
            Assert.Null(document.Selection);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task DraggingThroughColumnGutterSwitchesColumnsAtItsMidline()
    {
        using var service = new FakePdfDocumentService(1);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1000, Height = 700, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            PdfPageViewModel page = document.Pages[0];
            await WaitUntilAsync(() => page.Text is not null);
            // The right column deliberately comes first in reading order:
            // that is the case where a premature right-column caret would
            // visibly select right-column text.
            page.SetText(new Models.PdfTextPage(0, new Models.PdfRect(0, 800, 600, 0),
            [
                new Models.PdfTextCharacter(0, 'R', new Models.PdfRect(200, 750, 210, 738), new Models.PdfRect(199, 751, 211, 737)),
                new Models.PdfTextCharacter(1, 'S', new Models.PdfRect(200, 730, 210, 718), new Models.PdfRect(199, 731, 211, 717)),
                new Models.PdfTextCharacter(2, 'T', new Models.PdfRect(200, 710, 210, 698), new Models.PdfRect(199, 711, 211, 697)),
                new Models.PdfTextCharacter(3, 'L', new Models.PdfRect(20, 750, 30, 738), new Models.PdfRect(19, 751, 31, 737)),
                new Models.PdfTextCharacter(4, 'M', new Models.PdfRect(20, 730, 30, 718), new Models.PdfRect(19, 731, 31, 717)),
                new Models.PdfTextCharacter(5, 'N', new Models.PdfRect(20, 710, 30, 698), new Models.PdfRect(19, 711, 31, 697)),
            ]));
            Border surface = PageSurface(view, 0);
            Point press = PagePoint(surface, 22, 744, window);
            window.MouseDown(press, MouseButton.Left);

            // x=80 is left of the gutter midline (x=115): stay in the left column.
            Point leftHalf = PagePoint(surface, 80, 744, window);
            window.MouseMove(leftHalf);
            Assert.NotNull(document.Selection);
            Assert.Equal(4, document.Selection.Value.End.CharacterIndex);
            Assert.Equal("L", await document.GetSelectedTextAsync());

            // x=150 is past the midline: the endpoint may now enter the right column.
            Point rightHalf = PagePoint(surface, 150, 744, window);
            window.MouseMove(rightHalf);
            Assert.NotNull(document.Selection);
            Assert.Equal(0, document.Selection.Value.Start.CharacterIndex);
            Assert.Equal("RST", await document.GetSelectedTextAsync());
            window.MouseUp(rightHalf, MouseButton.Left);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task DraggingOnShortLastLineStaysInLeftColumnUntilGutterMidline()
    {
        using var service = new FakePdfDocumentService(1);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1000, Height = 700, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            PdfPageViewModel page = document.Pages[0];
            await WaitUntilAsync(() => page.Text is not null);
            page.SetText(new Models.PdfTextPage(0, new Models.PdfRect(0, 800, 600, 0),
            [
                new Models.PdfTextCharacter(0, 'R', new Models.PdfRect(200, 750, 210, 738), new Models.PdfRect(199, 751, 211, 737)),
                new Models.PdfTextCharacter(1, 'S', new Models.PdfRect(200, 730, 210, 718), new Models.PdfRect(199, 731, 211, 717)),
                new Models.PdfTextCharacter(2, 'T', new Models.PdfRect(200, 710, 210, 698), new Models.PdfRect(199, 711, 211, 697)),
                // The first left line is a short paragraph last line.
                new Models.PdfTextCharacter(3, 'S', new Models.PdfRect(20, 750, 30, 738), new Models.PdfRect(19, 751, 31, 737)),
                new Models.PdfTextCharacter(4, 'a', new Models.PdfRect(20, 730, 30, 718), new Models.PdfRect(19, 731, 31, 717)),
                new Models.PdfTextCharacter(5, 'b', new Models.PdfRect(30, 730, 40, 718), new Models.PdfRect(29, 731, 41, 717)),
                new Models.PdfTextCharacter(6, 'c', new Models.PdfRect(40, 730, 50, 718), new Models.PdfRect(39, 731, 51, 717)),
                new Models.PdfTextCharacter(7, 'd', new Models.PdfRect(50, 730, 60, 718), new Models.PdfRect(49, 731, 61, 717)),
                new Models.PdfTextCharacter(8, 'e', new Models.PdfRect(60, 730, 70, 718), new Models.PdfRect(59, 731, 71, 717)),
                new Models.PdfTextCharacter(9, 'f', new Models.PdfRect(70, 730, 80, 718), new Models.PdfRect(69, 731, 81, 717)),
                new Models.PdfTextCharacter(10, 'a', new Models.PdfRect(20, 710, 30, 698), new Models.PdfRect(19, 711, 31, 697)),
                new Models.PdfTextCharacter(11, 'b', new Models.PdfRect(30, 710, 40, 698), new Models.PdfRect(29, 711, 41, 697)),
                new Models.PdfTextCharacter(12, 'c', new Models.PdfRect(40, 710, 50, 698), new Models.PdfRect(39, 711, 51, 697)),
                new Models.PdfTextCharacter(13, 'd', new Models.PdfRect(50, 710, 60, 698), new Models.PdfRect(49, 711, 61, 697)),
                new Models.PdfTextCharacter(14, 'e', new Models.PdfRect(60, 710, 70, 698), new Models.PdfRect(59, 711, 71, 697)),
                new Models.PdfTextCharacter(15, 'f', new Models.PdfRect(70, 710, 80, 698), new Models.PdfRect(69, 711, 81, 697)),
            ]));
            Border surface = PageSurface(view, 0);
            Point press = PagePoint(surface, 22, 744, window);
            window.MouseDown(press, MouseButton.Left);

            // The short line's own gap midpoint is x=115, but the gutter shared
            // with the next line is [70,200] (midline 135). At x=120 the pointer
            // is on the left side of the real gutter and must not select the
            // right column.
            Point leftHalf = PagePoint(surface, 120, 744, window);
            window.MouseMove(leftHalf);
            Assert.NotNull(document.Selection);
            Assert.Equal(4, document.Selection.Value.End.CharacterIndex);
            Assert.Equal("S", await document.GetSelectedTextAsync());

            Point rightHalf = PagePoint(surface, 150, 744, window);
            window.MouseMove(rightHalf);
            Assert.NotNull(document.Selection);
            Assert.Equal(0, document.Selection.Value.Start.CharacterIndex);
            Assert.Equal("RST", await document.GetSelectedTextAsync());
            window.MouseUp(rightHalf, MouseButton.Left);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task DraggingThroughStaggeredGutterStaysLeftUntilMidline()
    {
        using var service = new FakePdfDocumentService(1);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1000, Height = 700, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            PdfPageViewModel page = document.Pages[0];
            await WaitUntilAsync(() => page.Text is not null);
            page.SetText(new Models.PdfTextPage(0, new Models.PdfRect(0, 800, 600, 0),
            [
                new Models.PdfTextCharacter(0, 'A', new Models.PdfRect(20, 750, 30, 738), new Models.PdfRect(19, 751, 31, 737)),
                new Models.PdfTextCharacter(1, 'B', new Models.PdfRect(20, 700, 30, 688), new Models.PdfRect(19, 701, 31, 687)),
                new Models.PdfTextCharacter(2, 'R', new Models.PdfRect(400, 750, 410, 738), new Models.PdfRect(399, 751, 411, 737)),
                new Models.PdfTextCharacter(3, 'S', new Models.PdfRect(400, 725, 410, 713), new Models.PdfRect(399, 726, 411, 712)),
                new Models.PdfTextCharacter(4, 'T', new Models.PdfRect(400, 700, 410, 688), new Models.PdfRect(399, 701, 411, 687)),
            ]));
            Border surface = PageSurface(view, 0);
            Point left = PagePoint(surface, 22, 744, window);
            window.MouseDown(left, MouseButton.Left);

            // The right-only line at y=719 has no left counterpart, but the
            // gutter of the surrounding lines has its midline at x=215.
            // Left of it, the endpoint must not jump to the right column.
            Point gap = PagePoint(surface, 100, 719, window);
            window.MouseMove(gap);
            Assert.Null(document.Selection);

            Point right = PagePoint(surface, 300, 719, window);
            window.MouseMove(right);
            Assert.Equal(3, document.Selection!.Value.End.CharacterIndex);
            window.MouseUp(right, MouseButton.Left);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task MouseSelectionContinuesAcrossPageBoundary()
    {
        using var service = new FakePdfDocumentService(2);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1000, Height = 700, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            await WaitUntilAsync(() => document.Pages[0].Text is not null);
            Border firstSurface = PageSurface(view, 0);
            Point first = PagePoint(firstSurface, 22, 744, window);
            window.MouseDown(first, MouseButton.Left);

            view.PageItems.ScrollIntoView(1);
            window.UpdateLayout();
            await WaitUntilAsync(() => document.Pages[1].Text is not null);
            Border secondSurface = PageSurface(view, 1);
            Point second = PagePoint(secondSurface, 40, 744, window);
            window.MouseMove(second);
            window.MouseUp(second, MouseButton.Left);

            Assert.Equal(0, document.Selection!.Value.Start.PageIndex);
            Assert.Equal(1, document.Selection.Value.End.PageIndex);
            Assert.Equal(2, document.Selection.Value.End.CharacterIndex);
            Assert.Equal(
                "PDF text" + Environment.NewLine + Environment.NewLine + "PD",
                await document.GetSelectedTextAsync());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task HighlightModeClickSelectsAWordWithoutDisablingScrolling()
    {
        using var service = new FakePdfDocumentService(3);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1000, Height = 700, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            await WaitUntilAsync(() => document.Pages[0].Text is not null);
            document.IsHighlightMode = true;
            Border surface = PageSurface(view, 0);
            Point word = PagePoint(surface, 35, 744, window);
            window.MouseDown(word, MouseButton.Left);
            window.MouseUp(word, MouseButton.Left);
            await WaitUntilAsync(() => document.Highlights.Count == 1);

            Assert.Equal(new Models.PdfRect(20, 750, 50, 738), document.Highlights[0].Rects[0]);
            Vector before = view.DocumentScroller.Offset;
            window.MouseWheel(new Point(500, 500), new Vector(0, -3), RawInputModifiers.None);
            window.UpdateLayout();
            Assert.True(view.DocumentScroller.Offset.Y > before.Y);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task PointerBetweenLinesKeepsThePreviousSelectionEndpoint()
    {
        using var service = new FakePdfDocumentService(1);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1000, Height = 700, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            PdfPageViewModel page = document.Pages[0];
            await WaitUntilAsync(() => page.Text is not null);
            page.SetText(new Models.PdfTextPage(0, new Models.PdfRect(0, 800, 600, 0),
            [
                new Models.PdfTextCharacter(0, 'A', new Models.PdfRect(20, 750, 30, 738), new Models.PdfRect(19, 751, 31, 737)),
                new Models.PdfTextCharacter(1, 'B', new Models.PdfRect(30, 750, 40, 738), new Models.PdfRect(29, 751, 41, 737)),
                new Models.PdfTextCharacter(2, 'C', new Models.PdfRect(20, 730, 30, 718), new Models.PdfRect(19, 731, 31, 717)),
                new Models.PdfTextCharacter(3, 'D', new Models.PdfRect(30, 730, 40, 718), new Models.PdfRect(29, 731, 41, 717)),
            ]));
            Border surface = PageSurface(view, 0);
            Point start = PagePoint(surface, 22, 744, window);
            Point end = PagePoint(surface, 40, 744, window);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(end);
            Assert.Equal(2, document.Selection!.Value.End.CharacterIndex);

            Point betweenLines = PagePoint(surface, 30, 734, window);
            window.MouseMove(betweenLines);
            Assert.Equal(0, document.Selection!.Value.Start.CharacterIndex);
            Assert.Equal(2, document.Selection.Value.End.CharacterIndex);
            window.MouseUp(betweenLines, MouseButton.Left);
            Assert.Equal("AB", await document.GetSelectedTextAsync());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task HighlightToolbarClickDoesNotFallThroughToTextBehindIt()
    {
        using var service = new FakePdfDocumentService(1);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1000, Height = 700, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            await WaitUntilAsync(() => document.Pages[0].Text is not null);
            ToggleButton button = view.HighlightButton;
            Border surface = PageSurface(view, 0);
            Point center = button.TranslatePoint(
                new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            Point local = window.TranslatePoint(center, surface)!.Value;
            double scale = DocumentTabViewModel.LayoutScale;
            double x = local.X / scale;
            double y = 800 - local.Y / scale;
            document.Pages[0].SetText(new Models.PdfTextPage(0, new Models.PdfRect(0, 800, 600, 0),
            [
                new Models.PdfTextCharacter(0, 'A',
                    new Models.PdfRect(x - 8, y + 8, x + 8, y - 8),
                    new Models.PdfRect(x - 9, y + 9, x + 9, y - 9)),
            ]));

            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left);
            window.UpdateLayout();

            Assert.True(document.IsHighlightMode);
            Assert.True(button.IsChecked);
            Assert.Null(document.Selection);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task StartupLoadsOnlyVisibleTextAndSearchLoadsTheRemainder()
    {
        using var service = new FakePdfDocumentService(20);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        Assert.Empty(service.TextPages);
        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1000, Height = 700, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            await WaitUntilAsync(() => service.TextPages.Length > 0);
            Assert.True(service.TextPages.Distinct().Count() < document.PageCount);

            document.SearchQuery = "PDF";
            await WaitUntilAsync(() => !document.IsSearching && document.SearchResults.Count == 20);
            Assert.Equal(20, service.TextPages.Distinct().Count());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task RightClickDeletesNewAndExistingHighlightsAtTheZoomedPosition()
    {
        using var service = new FakePdfDocumentService(1);
        service.InitialHighlights.Add(new Models.PdfHighlight(
            0,
            [new Models.PdfRect(20, 750, 50, 738)],
            Avalonia.Media.Colors.Yellow));
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1000, Height = 700, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            await WaitUntilAsync(() => document.Pages[0].Text is not null);
            document.Zoom = 1.5;
            window.UpdateLayout();
            await document.AddWordHighlightAsync(0, 1);
            Models.PdfHighlight added = document.Highlights.Last();
            Border surface = PageSurface(view, 0);
            Point point = PagePoint(surface, 35, 744, window);

            window.MouseDown(point, MouseButton.Right);
            window.UpdateLayout();
            Assert.Same(added, document.SelectedHighlight);
            Assert.True(view.HighlightContextMenu!.IsOpen);
            DeleteHighlightMenuItem(view).RaiseEvent(
                new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Single(document.Highlights);
            Assert.Null(document.SelectedHighlight);

            window.MouseDown(point, MouseButton.Right);
            window.UpdateLayout();
            Assert.Same(document.Highlights[0], document.SelectedHighlight);
            DeleteHighlightMenuItem(view).RaiseEvent(
                new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Empty(document.Highlights);
            Assert.True(document.HasUnsavedHighlights);
            Assert.Equal(0, service.SaveCallCount);
        }
        finally
        {
            view.HighlightContextMenu?.Hide();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task SearchResultClickScrollsToItsPageAndSelectsMatch()
    {
        using var service = new FakePdfDocumentService(4);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1100, Height = 700, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            document.ShowSearch();
            document.SearchQuery = "text";
            await WaitUntilAsync(() => document.SearchResults.Count == 4 && !document.IsSearching);
            window.UpdateLayout();

            view.SearchResultsList.SelectedItem = document.SearchResults[3];
            window.UpdateLayout();

            Assert.Equal(4, document.CurrentPage);
            Assert.Equal(3, document.Selection!.Value.Start.PageIndex);
            Assert.True(view.DocumentScroller.Offset.Y > 0);
        }
        finally
        {
            window.Close();
        }
    }

    private static MenuItem DeleteHighlightMenuItem(PdfDocumentView view) =>
        view.HighlightContextMenu!.Items.OfType<MenuItem>().Single();

    private static Border PageSurface(PdfDocumentView view, int pageIndex) => view.PageItems
        .GetRealizedContainers()
        .Where(container => container.DataContext is PdfPageViewModel page && page.PageIndex == pageIndex)
        .Select(container => container.GetVisualDescendants().OfType<Border>()
            .First(border => border.Name == "PageSurface"))
        .Single();

    private static Point PagePoint(Control surface, double pdfX, double pdfY, Window window)
    {
        var page = (PdfPageViewModel)surface.DataContext!;
        Models.PdfTextPage text = page.Text!;
        double scale = DocumentTabViewModel.LayoutScale * page.Zoom;
        return surface.TranslatePoint(
            new Point((pdfX - text.CropBox.L) * scale, (text.CropBox.T - pdfY) * scale),
            window)!.Value;
    }
}
