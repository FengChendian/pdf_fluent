using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using pdf_fluent.ViewModels;
using pdf_fluent.Views;
using Xunit;
using static pdf_fluent.Tests.PdfTestHelpers;

namespace pdf_fluent.Tests;

public sealed class PdfPageNavigationTests
{
    [AvaloniaFact]
    public async Task PageJumpPanelFadesAndSlidesInLikeTheTableOfContents()
    {
        using var service = new FakePdfDocumentService(pageCount: 3);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1200, Height = 900, Content = view };

        try
        {
            window.Show();
            window.UpdateLayout();
            var overlay = Named<Grid>(view, "PageJumpOverlay");
            var indicator = Named<TextBlock>(view, "PageIndicatorText");
            var input = Named<TextBox>(view, "PageJumpTextBox");
            var viewport = Named<Grid>(view, "DocumentViewport");
            Rect viewportBounds = viewport.Bounds;
            Assert.False(overlay.IsVisible);
            Assert.Equal(0, overlay.Opacity);
            Assert.Equal(new Thickness(0, 33, 16, 0), overlay.Margin);

            Click(window, indicator);
            window.UpdateLayout();
            Assert.True(overlay.IsVisible);
            Assert.True(input.IsKeyboardFocusWithin);
            Assert.True(overlay.Opacity < 1);
            Assert.True(overlay.Margin.Top < 41);

            await Task.Delay(260);
            window.UpdateLayout();
            Assert.Equal(1, overlay.Opacity);
            Assert.Equal(new Thickness(0, 41, 16, 0), overlay.Margin);
            Assert.Equal(viewportBounds, viewport.Bounds);
            var panel = Named<Border>(view, "PageJumpPanel");
            var header = Named<Border>(view, "ReaderHeader");
            Assert.Equal(header.Bounds.Bottom + 1, panel.TranslatePoint(default, view)!.Value.Y, precision: 1);

            Click(window, indicator);
            Assert.False(overlay.IsVisible);
            await Task.Delay(260);
            Click(window, indicator);
            window.UpdateLayout();
            Assert.True(overlay.IsVisible);
            Assert.True(overlay.Opacity < 1);
            Assert.True(overlay.Margin.Top < 41);
            await Task.Delay(260);
            window.UpdateLayout();
            Assert.Equal(1, overlay.Opacity);
            Assert.Equal(new Thickness(0, 41, 16, 0), overlay.Margin);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public async Task DisabledPageArrowsDoNotHoverOrActivateThePageJumpButton(int pageCount, bool atEnd)
    {
        using var service = new FakePdfDocumentService(pageCount);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1200, Height = 900, Content = view };

        try
        {
            window.Show();
            window.UpdateLayout();
            if (atEnd)
            {
                view.DocumentScroller.Offset = new Vector(0, view.DocumentScroller.Extent.Height);
                window.UpdateLayout();
            }

            Assert.Equal(atEnd ? pageCount : 1, document.CurrentPage);
            var jump = Named<ToggleButton>(view, "PageJumpButton");
            var jumpSurface = Named<ContentPresenter>(jump, "PART_ContentPresenter");
            var previous = Named<Button>(view, "PreviousPageButton");
            var next = Named<Button>(view, "NextPageButton");
            var indicator = Named<TextBlock>(view, "PageIndicatorText");

            foreach (Button arrow in new[] { previous, next })
            {
                var arrowSurface = Named<ContentPresenter>(arrow, "PART_ContentPresenter");
                for (int move = 0; move < 5; move++)
                {
                    window.MouseMove(Center(indicator, window));
                    Assert.True(jump.IsPointerOver);
                    Assert.Equal(Color.Parse("#F0F2F8"), BrushColor(jumpSurface));

                    window.MouseMove(Center(arrow, window));
                    window.UpdateLayout();
                    Assert.False(jump.IsPointerOver);
                    Assert.Equal(Colors.Transparent, BrushColor(jumpSurface));
                    Assert.Equal(arrow.IsEnabled ? Color.Parse("#F0F2F8") : Colors.Transparent,
                        BrushColor(arrowSurface));
                }

                if (!arrow.IsEnabled)
                {
                    Click(window, arrow);
                    window.UpdateLayout();
                    Assert.False(view.IsPageJumpVisible);
                    Assert.False(jump.IsChecked);
                }
            }

            Click(window, indicator);
            Assert.True(view.IsPageJumpVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClampedPageNavigationUsesTheDisplayedPageWhenReversingDirection(bool rapidClicks)
    {
        using var service = new FakePdfDocumentService(pageCount: 5);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        document.Zoom = DocumentTabViewModel.MinZoom;
        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1200, Height = 1400, Content = view };

        try
        {
            window.Show();
            window.UpdateLayout();
            var next = Named<Button>(view, "NextPageButton");
            var previous = Named<Button>(view, "PreviousPageButton");

            // Several whole pages fit below the current page at the bottom.
            view.DocumentScroller.Offset = new Vector(0, view.DocumentScroller.Extent.Height);
            window.UpdateLayout();
            Assert.Equal("3 / 5", document.PageIndicator);
            double bottomOffset = view.DocumentScroller.Offset.Y;

            for (int click = 0; click < 6; click++)
            {
                Click(window, next);
                if (!rapidClicks)
                {
                    window.UpdateLayout();
                    Assert.Equal("3 / 5", document.PageIndicator);
                }
            }

            window.UpdateLayout();
            Assert.Equal(bottomOffset, view.DocumentScroller.Offset.Y);
            Assert.Equal("3 / 5", document.PageIndicator);

            Click(window, previous);
            window.UpdateLayout();
            Assert.Equal("2 / 5", document.PageIndicator);
            Assert.True(view.DocumentScroller.Offset.Y < bottomOffset);
            Click(window, previous);
            window.UpdateLayout();
            Assert.Equal("1 / 5", document.PageIndicator);
            Assert.False(previous.IsEnabled);
        }
        finally { window.Close(); }
    }

    private static Color BrushColor(ContentPresenter presenter) =>
        Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background).Color;
}
