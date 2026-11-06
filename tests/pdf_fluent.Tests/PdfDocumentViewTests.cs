using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using pdf_fluent.ViewModels;
using pdf_fluent.Views;
using Xunit;

namespace pdf_fluent.Tests;

public sealed class PdfDocumentViewTests
{
    [AvaloniaFact]
    public async Task CtrlWheelChangesZoomWhilePlainWheelDoesNot()
    {
        using var service = new FakePdfDocumentService(pageCount: 3);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();

        var view = new PdfDocumentView { DataContext = document };
        var window = new Window
        {
            Width = 1200,
            Height = 900,
            Content = view,
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            window.MouseWheel(new Point(500, 500), new Vector(0, 1), RawInputModifiers.None);
            Assert.Equal(100, document.ZoomPercent);

            window.MouseWheel(new Point(500, 500), new Vector(0, 1), RawInputModifiers.Control);
            view.ZoomController!.FinishAnimation();
            Assert.Equal(125, document.ZoomPercent);

            window.MouseWheel(new Point(500, 500), new Vector(0, -1), RawInputModifiers.Control);
            view.ZoomController!.FinishAnimation();
            Assert.Equal(100, document.ZoomPercent);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ZoomControlsAreCenteredAndEvenlySpacedWithoutRenderingLabels()
    {
        using var service = new FakePdfDocumentService(pageCount: 1);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();

        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1200, Height = 900, Content = view };

        try
        {
            window.Show();
            window.UpdateLayout();

            Grid documentSurface = Assert.IsType<Grid>(view.Content);
            Assert.Equal(
                Avalonia.Media.Color.Parse("#EEF1F8"),
                Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(documentSurface.Background).Color);
            Grid zoomControls = view.GetVisualDescendants()
                .OfType<Grid>()
                .Single(grid => grid.Name == "ZoomControls");
            Button zoomOut = zoomControls.Children.OfType<Button>().Single(button => button.Name == "ZoomOutButton");
            Button zoomIn = zoomControls.Children.OfType<Button>().Single(button => button.Name == "ZoomInButton");
            TextBlock percentage = zoomControls.Children.OfType<TextBlock>().Single();
            double[] centers = new Control[] { zoomOut, percentage, zoomIn }
                .Select(control => control.TranslatePoint(
                    new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), zoomControls)!.Value.X)
                .ToArray();

            Assert.Equal(44, centers[1] - centers[0], precision: 1);
            Assert.Equal(44, centers[2] - centers[1], precision: 1);
            Assert.Equal(66, (centers[0] + centers[2]) / 2, precision: 1);

            Point zoomOutCenter = zoomOut.TranslatePoint(
                new Point(zoomOut.Bounds.Width / 2, zoomOut.Bounds.Height / 2), window)!.Value;
            window.MouseMove(zoomOutCenter);
            Assert.Equal(
                Avalonia.Media.Color.Parse("#F0F2F8"),
                Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(zoomOut.Background).Color);
            Avalonia.Controls.Presenters.ContentPresenter zoomOutSurface = zoomOut.GetVisualChildren()
                .OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                .Single();
            Assert.Equal(
                Avalonia.Media.Color.Parse("#F0F2F8"),
                Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(zoomOutSurface.Background).Color);

            var minus = Assert.IsAssignableFrom<Optris.Icons.Avalonia.Icon>(zoomOut.Content);
            Assert.Equal("fa-solid fa-minus", minus.Value);
            Assert.Equal(11, minus.FontSize);
            Assert.NotNull(minus.Foreground);

            foreach (Button button in new[] { zoomOut, zoomIn })
            {
                var icon = Assert.IsAssignableFrom<Control>(button.Content);
                Point iconCenter = icon.TranslatePoint(
                    new Point(icon.Bounds.Width / 2, icon.Bounds.Height / 2), button)!.Value;
                Assert.InRange(iconCenter.X, button.Bounds.Width / 2 - 0.75, button.Bounds.Width / 2 + 0.75);
                Assert.InRange(iconCenter.Y, button.Bounds.Height / 2 - 0.75, button.Bounds.Height / 2 + 0.75);
            }

            string?[] labels = view.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToArray();
            Assert.DoesNotContain("连续滚动", labels);
            Assert.DoesNotContain("按需渲染", labels);
            Assert.DoesNotContain("页面将在滚动时按需渲染", labels);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ReaderOverlaysHaveThinBordersAndFullyRoundedEnds()
    {
        using var service = new FakePdfDocumentService(pageCount: 1);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1200, Height = 900, Content = view };

        try
        {
            window.Show();
            window.UpdateLayout();

            Border[] overlays = view.GetVisualDescendants().OfType<Border>()
                .Where(border => border.Name is "ReaderToolbar" or "PageNavigation" or "PageIndicatorOverlay")
                .ToArray();
            Assert.Equal(3, overlays.Length);
            Assert.All(overlays, overlay =>
            {
                Assert.Equal(new Thickness(1), overlay.BorderThickness);
                Assert.Equal(
                    Avalonia.Media.Color.Parse("#DDE2EC"),
                    Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(overlay.BorderBrush).Color);
            });
            Assert.All(overlays.Where(overlay => overlay.Name != "PageIndicatorOverlay"), overlay =>
                Assert.True(overlay.CornerRadius.TopLeft >= overlay.Bounds.Height / 2));
            Assert.Equal(
                new CornerRadius(16),
                overlays.Single(overlay => overlay.Name == "PageIndicatorOverlay").CornerRadius);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ReaderToolbarAndPdfPageShareTheSameHorizontalCenter()
    {
        using var service = new FakePdfDocumentService(pageCount: 1);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1200, Height = 900, Content = view };

        try
        {
            window.Show();
            window.UpdateLayout();

            Border toolbar = view.GetVisualDescendants().OfType<Border>()
                .Single(border => border.Name == "ReaderToolbar");
            Border page = view.GetVisualDescendants().OfType<Border>()
                .First(border => border.Classes.Contains("page-surface"));
            double toolbarCenter = toolbar.TranslatePoint(
                new Point(toolbar.Bounds.Width / 2, 0), view)!.Value.X;
            double pageCenter = page.TranslatePoint(new Point(page.Bounds.Width / 2, 0), view)!.Value.X;

            Assert.Equal(pageCenter, toolbarCenter, precision: 1);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task FloatingPageIndicatorIsBottomRightAndRightAlignedWithPageNavigation()
    {
        using var service = new FakePdfDocumentService(pageCount: 3);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1200, Height = 900, Content = view };

        try
        {
            window.Show();
            view.ShowPageIndicator();
            window.UpdateLayout();
            Border navigation = view.GetVisualDescendants().OfType<Border>()
                .Single(border => border.Name == "PageNavigation");
            Border indicator = view.GetVisualDescendants().OfType<Border>()
                .Single(border => border.Name == "PageIndicatorOverlay");
            Point navigationTopRight = navigation.TranslatePoint(
                new Point(navigation.Bounds.Width, 0), view)!.Value;
            Point indicatorBottomRight = indicator.TranslatePoint(
                new Point(indicator.Bounds.Width, indicator.Bounds.Height), view)!.Value;

            Assert.Equal(navigationTopRight.X, indicatorBottomRight.X, precision: 1);
            Assert.Equal(16, navigationTopRight.Y, precision: 1);
            Assert.Equal(view.Bounds.Width - 20, navigationTopRight.X, precision: 1);
            Assert.Equal(view.Bounds.Height - 20, indicatorBottomRight.Y, precision: 1);
            Assert.True(indicatorBottomRight.Y > view.Bounds.Height / 2);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task PageGapIsHalfOfItsOriginalHeight()
    {
        using var service = new FakePdfDocumentService(pageCount: 2);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1200, Height = 900, Content = view };

        try
        {
            window.Show();
            window.UpdateLayout();
            var panel = view.GetVisualDescendants()
                .OfType<pdf_fluent.Controls.PdfVirtualizingPanel>()
                .Single();
            double firstPageBottom = panel.ItemTopAt(0, panel.Zoom) +
                                     PdfPageViewModel.PageHeightAt(document.Pages[0], panel.Zoom);
            double secondPageTop = panel.ItemTopAt(1, panel.Zoom);

            Assert.Equal(16.5, secondPageTop - firstPageBottom, precision: 1);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task FloatingPageIndicatorShowsOnlyWhenThePageChangesAndHidesAfterTheDelay()
    {
        using var service = new FakePdfDocumentService(pageCount: 3);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        var view = new PdfDocumentView { DataContext = document };
        Assert.Equal(TimeSpan.FromMilliseconds(800), view.PageIndicatorAutoHideDelay);
        view.PageIndicatorAutoHideDelay = TimeSpan.FromMilliseconds(60);
        var window = new Window { Width = 1200, Height = 900, Content = view };

        try
        {
            window.Show();
            window.UpdateLayout();
            Assert.False(view.IsPageIndicatorVisible);

            await document.LoadAsync();
            window.UpdateLayout();
            Assert.False(view.IsPageIndicatorVisible);

            Button next = view.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Name == "NextPageButton");
            next.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Assert.Equal(2, document.CurrentPage);
            Assert.True(view.IsPageIndicatorVisible);

            await Task.Delay(180);
            Assert.False(view.IsPageIndicatorVisible);

            next.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Assert.Equal(3, document.CurrentPage);
            Assert.True(view.IsPageIndicatorVisible);

            await Task.Delay(180);
            Assert.False(view.IsPageIndicatorVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task PageNavigationChangesTheCurrentPageAndScrollsTheDocument()
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
            Button next = view.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Name == "NextPageButton");
            Button previous = view.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Name == "PreviousPageButton");

            Assert.False(previous.IsEnabled);
            Assert.True(next.IsEnabled);
            next.Command?.Execute(null);
            Point nextCenter = next.TranslatePoint(new Point(next.Bounds.Width / 2, next.Bounds.Height / 2), window)!.Value;
            window.MouseDown(nextCenter, MouseButton.Left);
            window.MouseUp(nextCenter, MouseButton.Left);
            window.UpdateLayout();

            Assert.Equal(2, document.CurrentPage);
            Assert.Equal("2 / 3", document.PageIndicator);
            Assert.True(previous.IsEnabled);
            Assert.True(view.IsPageIndicatorVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task PageNavigationSupportsConsecutiveNextAndPreviousClicks()
    {
        using var service = new FakePdfDocumentService(pageCount: 4);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1200, Height = 900, Content = view };

        try
        {
            window.Show();
            window.UpdateLayout();
            Button next = view.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Name == "NextPageButton");
            Button previous = view.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Name == "PreviousPageButton");

            for (int expectedPage = 2; expectedPage <= 4; expectedPage++)
            {
                Point center = next.TranslatePoint(
                    new Point(next.Bounds.Width / 2, next.Bounds.Height / 2), window)!.Value;
                window.MouseDown(center, MouseButton.Left);
                window.MouseUp(center, MouseButton.Left);
                window.UpdateLayout();
                Assert.Equal(expectedPage, document.CurrentPage);
            }

            Assert.False(next.IsEnabled);
            for (int expectedPage = 3; expectedPage >= 1; expectedPage--)
            {
                Point center = previous.TranslatePoint(
                    new Point(previous.Bounds.Width / 2, previous.Bounds.Height / 2), window)!.Value;
                window.MouseDown(center, MouseButton.Left);
                window.MouseUp(center, MouseButton.Left);
                window.UpdateLayout();
                Assert.Equal(expectedPage, document.CurrentPage);
            }

            Assert.False(previous.IsEnabled);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ConsecutivePageClicksIgnoreStaleLayoutPageUpdates()
    {
        using var service = new FakePdfDocumentService(pageCount: 4);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1200, Height = 900, Content = view };

        try
        {
            window.Show();
            window.UpdateLayout();
            Button next = view.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Name == "NextPageButton");
            next.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(2, document.CurrentPage);

            document.SyncRealizedPages([document.Pages[0]], [document.Pages[0]]);
            Assert.Equal(1, document.CurrentPage);

            next.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(3, document.CurrentPage);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CurrentPageChangesAfterCrossingHalfOfThePageGap()
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
            var panel = view.GetVisualDescendants()
                .OfType<pdf_fluent.Controls.PdfVirtualizingPanel>()
                .Single();
            double secondPageTop = panel.Padding.Top + panel.ItemTopAt(1, panel.Zoom);
            double halfGap = PdfPageViewModel.FooterHeight / 2;

            view.DocumentScroller.Offset = new Vector(0, secondPageTop - halfGap - 1);
            window.UpdateLayout();
            Assert.Equal(1, document.CurrentPage);

            view.DocumentScroller.Offset = new Vector(0, secondPageTop - halfGap + 1);
            window.UpdateLayout();
            Assert.Equal(2, document.CurrentPage);

            view.DocumentScroller.Offset = new Vector(0, secondPageTop);
            window.UpdateLayout();
            Assert.Equal(2, document.CurrentPage);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task DisabledToolButtonsStayTransparentAndUseLightGrayLineIcons()
    {
        using var service = new FakePdfDocumentService(pageCount: 1);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1200, Height = 900, Content = view };

        try
        {
            window.Show();
            window.UpdateLayout();

            Assert.True(view.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SearchButton").IsEnabled);
            foreach (string name in new[] { "AnnotationButton", "DownloadButton" })
            {
                Button button = view.GetVisualDescendants().OfType<Button>()
                    .Single(candidate => candidate.Name == name);
                Assert.False(button.IsEnabled);
                Assert.Equal(1, button.Opacity);
                Assert.Equal(
                    Avalonia.Media.Color.Parse("#C4C8D2"),
                    Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(button.Foreground).Color);
                Assert.Equal(
                    Avalonia.Media.Colors.Transparent,
                    Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(button.Background).Color);

                var icon = Assert.IsAssignableFrom<Optris.Icons.Avalonia.Icon>(button.Content);
                Assert.Equal(14, icon.FontSize);
                Assert.Equal(
                    Avalonia.Media.Color.Parse("#C4C8D2"),
                    Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(icon.Foreground).Color);

                Point center = button.TranslatePoint(
                    new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                window.MouseMove(center);
                var surface = button.GetVisualChildren()
                    .OfType<Avalonia.Controls.Presenters.ContentPresenter>().Single();
                Assert.Equal(
                    Avalonia.Media.Colors.Transparent,
                    Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(surface.Background).Color);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task MoreToolsOpensTheFavoriteActionWithTheNavigationStarIcon()
    {
        using var viewModel = new MainViewModel();
        using var service = new FakePdfDocumentService(pageCount: 1);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        viewModel.OpenDocuments.Add(document);
        viewModel.SelectedDocument = document;
        var window = new MainWindow { DataContext = viewModel };
        MenuFlyout? flyout = null;

        try
        {
            window.Show();
            window.UpdateLayout();

            Button more = window.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Name == "MoreToolsButton");
            Assert.True(more.IsEnabled);
            Assert.Equal("fa-solid fa-ellipsis",
                Assert.IsAssignableFrom<Optris.Icons.Avalonia.Icon>(more.Content).Value);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), button => button.Name == "FavoriteButton");

            Point center = more.TranslatePoint(new Point(more.Bounds.Width / 2, more.Bounds.Height / 2), window)!.Value;
            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left);
            window.UpdateLayout();

            flyout = Assert.IsAssignableFrom<MenuFlyout>(more.Flyout);
            Assert.True(flyout.IsOpen);
            MenuItem favorite = flyout.Items.OfType<MenuItem>().Single();
            MenuFlyoutPresenter presenter = favorite.GetVisualAncestors()
                .OfType<MenuFlyoutPresenter>()
                .Single();
            Assert.Equal(
                Avalonia.Media.Colors.White,
                Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(presenter.Background).Color);
            Assert.Equal(new Thickness(0, 4), presenter.Padding);
            Assert.Equal(new CornerRadius(12), presenter.CornerRadius);
            Assert.Equal(-1, presenter.SelectedIndex);
            Assert.Equal(new Thickness(4, 0), favorite.Margin);
            Assert.Equal(new Thickness(10, 9), favorite.Padding);
            Assert.Equal(new CornerRadius(8), favorite.CornerRadius);
            Assert.Single(favorite.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "收藏文档");
            var menuIcon = favorite.GetVisualDescendants().OfType<Optris.Icons.Avalonia.Icon>()
                .Single(icon => icon.Name == "FavoriteMenuIcon");
            var navigationIcon = window.GetVisualDescendants().OfType<Optris.Icons.Avalonia.Icon>()
                .Single(icon => icon.Name == "FavoriteNavIcon");
            Assert.Equal("fa-regular fa-star", menuIcon.Value);
            Assert.Equal(navigationIcon.Value, menuIcon.Value);
        }
        finally
        {
            flyout?.Hide();
            window.Close();
        }
    }
}
