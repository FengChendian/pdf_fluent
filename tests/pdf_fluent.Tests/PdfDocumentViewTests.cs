using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using pdf_fluent.ViewModels;
using pdf_fluent.Views;
using Xunit;
using static pdf_fluent.Tests.PdfTestHelpers;
using Path = Avalonia.Controls.Shapes.Path;

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
    public async Task ReaderChromeUsesAContinuousSurfaceAndSeparateViewport()
    {
        using var service = new FakePdfDocumentService(3);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 968, Height = 652, Content = view };
        try
        {
            window.Show(); window.UpdateLayout();
            var header = Named<Border>(view, "ReaderHeader");
            var viewport = Named<Grid>(view, "DocumentViewport");
            Assert.Equal(48, header.Bounds.Height);
            Assert.Equal(new Thickness(0, 0, 0, 1), header.BorderThickness);
            Assert.Equal(Avalonia.Media.Colors.White, Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(header.Background).Color);
            Assert.Equal(header.Bounds.Bottom, viewport.TranslatePoint(default, view)!.Value.Y);
            Assert.True(viewport.ClipToBounds);
            var toolbar = Named<Border>(view, "ReaderToolbar");
            var navigation = Named<Border>(view, "PageNavigation");
            Assert.True(toolbar.TranslatePoint(new Point(toolbar.Bounds.Width, 0), view)!.Value.X <= navigation.TranslatePoint(default, view)!.Value.X);
            var tocButton = Named<Avalonia.Controls.Primitives.ToggleButton>(view, "TableOfContentsButton");
            var nextButton = Named<Button>(view, "NextPageButton");
            double leftInset = tocButton.TranslatePoint(default, view)!.Value.X;
            double rightInset = view.Bounds.Width -
                nextButton.TranslatePoint(new Point(nextButton.Bounds.Width, 0), view)!.Value.X;
            Assert.Equal(16, leftInset, precision: 1);
            Assert.Equal(leftInset, rightInset, precision: 1);
            view.DocumentScroller.Offset = new Vector(0, 900);
            window.UpdateLayout();
            Assert.Equal(48, viewport.TranslatePoint(default, view)!.Value.Y);
        }
        finally { window.Close(); }
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
    public async Task FloatingPageIndicatorSitsBottomRightWithoutChangingTheViewport()
    {
        using var service = new FakePdfDocumentService(3);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1200, Height = 900, Content = view };
        try
        {
            window.Show(); window.UpdateLayout();
            Size before = view.DocumentScroller.Viewport;
            view.ShowPageIndicator(); window.UpdateLayout();
            var indicator = Named<Border>(view, "PageIndicatorOverlay");
            Point bottomRight = indicator.TranslatePoint(
                new Point(indicator.Bounds.Width, indicator.Bounds.Height), view)!.Value;
            Assert.True(indicator.IsVisible);
            Assert.Equal(16, indicator.CornerRadius.TopLeft);
            Assert.Equal(view.Bounds.Width - 20, bottomRight.X, precision: 1);
            Assert.Equal(view.Bounds.Height - 20, bottomRight.Y, precision: 1);
            Assert.True(bottomRight.Y > view.Bounds.Height / 2);
            Assert.Equal(before, view.DocumentScroller.Viewport);
            view.HidePageIndicator(); window.UpdateLayout();
            Assert.Equal(before, view.DocumentScroller.Viewport);
            Assert.False(view.IsPageIndicatorVisible);
        }
        finally { window.Close(); }
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
            Assert.Equal("fa-solid fa-star", navigationIcon.Value);
        }
        finally
        {
            flyout?.Hide();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task PageIndicatorTogglesJumpPanelWithoutInterferingWithArrowButtons()
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
            var jumpButton = Named<Avalonia.Controls.Primitives.ToggleButton>(view, "PageJumpButton");
            TextBlock indicator = Named<TextBlock>(view, "PageIndicatorText");
            TextBox input = Named<TextBox>(view, "PageJumpTextBox");
            Button next = Named<Button>(view, "NextPageButton");

            Assert.False(view.IsPageJumpVisible);
            Click(window, next);
            window.UpdateLayout();
            Assert.Equal(2, document.CurrentPage);
            Assert.False(view.IsPageJumpVisible);
            Assert.False(jumpButton.IsChecked);

            Click(window, indicator);
            window.UpdateLayout();
            Assert.True(jumpButton.IsChecked);
            Assert.True(view.IsPageJumpVisible);
            Assert.Equal("2", input.Text);
            Assert.True(input.IsKeyboardFocusWithin);

            input.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Source = input,
                Key = Key.Escape,
            });
            Assert.False(view.IsPageJumpVisible);
            Assert.False(jumpButton.IsChecked);

            Click(window, indicator);
            Assert.True(view.IsPageJumpVisible);
            window.MouseDown(new Point(500, 500), MouseButton.Left);
            window.MouseUp(new Point(500, 500), MouseButton.Left);
            Assert.False(view.IsPageJumpVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task PageJumpNavigatesByButtonAndEnterAndKeepsInvalidInputOpen()
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
            TextBlock indicator = Named<TextBlock>(view, "PageIndicatorText");
            TextBox input = Named<TextBox>(view, "PageJumpTextBox");
            TextBlock error = Named<TextBlock>(view, "PageJumpError");

            Click(window, indicator);
            input.Text = "3";
            Click(window, Named<Button>(view, "GoToPageButton"));
            window.UpdateLayout();
            Assert.Equal(3, document.CurrentPage);
            Assert.False(view.IsPageJumpVisible);
            Assert.True(view.DocumentScroller.Offset.Y > 0);

            Click(window, indicator);
            input.Text = "5";
            input.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Source = input,
                Key = Key.Enter,
            });
            Assert.Equal(3, document.CurrentPage);
            Assert.True(view.IsPageJumpVisible);
            Assert.True(error.IsVisible);
            Assert.Equal("请输入 1 到 4 之间的页码", error.Text);

            input.Text = "2";
            input.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Source = input,
                Key = Key.Enter,
            });
            window.UpdateLayout();
            Assert.Equal(2, document.CurrentPage);
            Assert.False(view.IsPageJumpVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TocAndPageJumpPanelsShareTheSameTopOffset()
    {
        using var service = new FakePdfDocumentService(3);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1200, Height = 900, Content = view };
        try
        {
            window.Show(); window.UpdateLayout();
            double headerBottom = Named<Border>(view, "ReaderHeader")
                .TranslatePoint(new Point(0, Named<Border>(view, "ReaderHeader").Bounds.Height), view)!.Value.Y;
            Assert.Equal(48, headerBottom, precision: 1);

            var tocButton = Named<Avalonia.Controls.Primitives.ToggleButton>(view, "TableOfContentsButton");
            tocButton.IsChecked = true;
            window.UpdateLayout();
            await Task.Delay(260);
            window.UpdateLayout();
            Assert.Equal(headerBottom + 1,
                Named<Border>(view, "TableOfContentsPanel").TranslatePoint(default, view)!.Value.Y, precision: 1);
            Assert.Equal(headerBottom - 7,
                Named<Path>(view, "TableOfContentsPointer").TranslatePoint(default, view)!.Value.Y, precision: 1);

            tocButton.IsChecked = false;
            window.UpdateLayout();
            await Task.Delay(260);
            Click(window, Named<TextBlock>(view, "PageIndicatorText"));
            window.UpdateLayout();
            await Task.Delay(260);
            window.UpdateLayout();
            Assert.Equal(headerBottom + 1,
                Named<Border>(view, "PageJumpPanel").TranslatePoint(default, view)!.Value.Y, precision: 1);
            Assert.Equal(headerBottom - 7,
                Named<Path>(view, "PageJumpPointer").TranslatePoint(default, view)!.Value.Y, precision: 1);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task PageJumpPanelUsesTheBezierPointerAndFloatsOverThePage()
    {
        using var service = new FakePdfDocumentService(3);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1200, Height = 900, Content = view };
        try
        {
            window.Show(); window.UpdateLayout();
            Click(window, Named<TextBlock>(view, "PageIndicatorText"));
            window.UpdateLayout();
            await Task.Delay(260);
            window.UpdateLayout();
            var overlay = Named<Grid>(view, "PageJumpOverlay");
            var navigation = Named<Border>(view, "PageNavigation");
            var panel = Named<Border>(view, "PageJumpPanel");
            var pointer = Named<Path>(view, "PageJumpPointer");
            var pointerFill = Named<Path>(view, "PageJumpPointerFill");
            var tocPointer = Named<Path>(view, "TableOfContentsPointer");
            var tocPointerFill = Named<Path>(view, "TableOfContentsPointerFill");
            TextBlock indicator = Named<TextBlock>(view, "PageIndicatorText");
            TextBox input = Named<TextBox>(view, "PageJumpTextBox");
            Button goToPage = Named<Button>(view, "GoToPageButton");
            TextBlock error = Named<TextBlock>(view, "PageJumpError");

            Assert.True(overlay.IsVisible);
            Assert.Equal(navigation.Bounds.Width, overlay.Bounds.Width, precision: 1);
            Assert.Equal(new Thickness(0, 41, 16, 0), overlay.Margin);
            Assert.Equal(34, input.Bounds.Height);
            Assert.Equal(input.Bounds.Height, goToPage.Bounds.Height);
            var jumpButtonSurface = Named<Avalonia.Controls.Presenters.ContentPresenter>(goToPage, "PART_ContentPresenter");
            Assert.Equal(Avalonia.Media.Color.Parse("#6C5CE7"),
                Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(jumpButtonSurface.Background).Color);
            window.MouseMove(Center(goToPage, window));
            Assert.Equal(Avalonia.Media.Color.Parse("#5B4BD5"),
                Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(jumpButtonSurface.Background).Color);
            Assert.Equal(Avalonia.Media.Colors.White,
                Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(jumpButtonSurface.Foreground).Color);
            window.MouseMove(Center(input, window));
            Assert.Equal(Avalonia.Media.Color.Parse("#6C5CE7"),
                Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(jumpButtonSurface.Background).Color);
            Assert.Equal(new CornerRadius(12), panel.CornerRadius);
            Assert.Equal(new Thickness(1), panel.BorderThickness);
            Assert.Equal(
                Avalonia.Media.Colors.White,
                Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(panel.Background).Color);
            Assert.Equal(
                Avalonia.Media.Colors.White,
                Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(pointerFill.Fill).Color);
            Assert.Equal(tocPointer.Data?.ToString(), pointer.Data?.ToString());
            Assert.Equal(tocPointerFill.Data?.ToString(), pointerFill.Data?.ToString());
            Assert.Equal(Center(indicator, window).X, Center(pointer, window).X, precision: 1);

            double panelTop = panel.TranslatePoint(new Point(0, 0), view)!.Value.Y;
            Assert.Equal(Named<Border>(view, "ReaderHeader").Bounds.Bottom + 1, panelTop, precision: 1);
            Point pointerStartInPanel = pointer.TranslatePoint(new Point(0, 8), panel)!.Value;
            Assert.Equal(0, pointerStartInPanel.Y, precision: 1);
            Assert.InRange(pointerStartInPanel.X, 0, panel.Bounds.Width - 20);
            double pointerBottom = pointer.TranslatePoint(new Point(0, pointer.Bounds.Height), view)!.Value.Y;
            Assert.True(pointerBottom > panelTop);

            input.Text = "999";
            Click(window, goToPage);
            window.UpdateLayout();
            Assert.True(error.IsVisible);
            double errorBottom = error.TranslatePoint(new Point(0, error.Bounds.Height), view)!.Value.Y;
            double panelBottom = panel.TranslatePoint(new Point(0, panel.Bounds.Height), view)!.Value.Y;
            Assert.Equal(panelBottom - 12, errorBottom, precision: 1);
            input.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Source = input,
                Key = Key.Escape,
            });
            window.UpdateLayout();
            Assert.False(overlay.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ReaderSearchFloatsOverThePageWithoutShrinkingTheViewport()
    {
        using var service = new FakePdfDocumentService(3);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1200, Height = 900, Content = view };
        try
        {
            window.Show(); window.UpdateLayout();
            var viewport = Named<Grid>(view, "DocumentViewport");
            var viewportOrigin = viewport.TranslatePoint(default, view)!.Value;
            double viewportWidth = viewport.Bounds.Width;
            Button search = Named<Button>(view, "SearchButton");
            Click(window, search);
            window.UpdateLayout();

            Border panel = Named<Border>(view, "SearchPanel");
            Assert.True(panel.IsVisible);
            Assert.Equal(320, panel.Bounds.Width, precision: 1);
            Assert.Equal(new CornerRadius(12), panel.CornerRadius);
            Assert.Equal(new Thickness(1), panel.BorderThickness);
            Assert.Equal(
                Avalonia.Media.Colors.White,
                Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(panel.Background).Color);
            Assert.Equal(viewportOrigin, viewport.TranslatePoint(default, view)!.Value);
            Assert.Equal(viewportWidth, viewport.Bounds.Width);
            Assert.Equal(Avalonia.Layout.VerticalAlignment.Top, panel.VerticalAlignment);
            Assert.True(panel.TranslatePoint(default, view)!.Value.X > viewportOrigin.X);

            var title = Named<TextBlock>(view, "SearchPanelTitle");
            var close = Named<Button>(view, "CloseSearchButton");
            var closeIcon = close.GetVisualDescendants().OfType<Optris.Icons.Avalonia.Icon>().Single();
            Assert.InRange(Math.Abs(Center(title, window).Y - Center(closeIcon, window).Y), 0, 0.5);
            Point titlePosition = title.TranslatePoint(default, panel)!.Value;
            Assert.InRange(Math.Abs(titlePosition.X - titlePosition.Y), 0, 1);
            var results = Named<ListBox>(view, "SearchResultsList");
            var status = Named<TextBlock>(view, "SearchStatusText");
            Assert.False(results.IsVisible);
            Assert.Equal(panel.Bounds.Height - panel.Padding.Bottom - panel.BorderThickness.Bottom,
                status.TranslatePoint(new Point(0, status.Bounds.Height), panel)!.Value.Y, precision: 1);
            double emptyPanelHeight = panel.Bounds.Height;

            document.SearchQuery = "text";
            await WaitUntilAsync(() => !document.IsSearching && document.SearchResults.Count == 3);
            window.UpdateLayout();
            Assert.True(results.IsVisible);
            Assert.True(panel.Bounds.Height > emptyPanelHeight);

            document.SearchQuery = "no matching phrase";
            await WaitUntilAsync(() => !document.IsSearching);
            window.UpdateLayout();
            Assert.False(results.IsVisible);
            Assert.Equal("未找到匹配内容", status.Text);
            Assert.Equal(emptyPanelHeight, panel.Bounds.Height, precision: 1);

            document.SearchQuery = string.Empty;
            window.UpdateLayout();
            Assert.False(results.IsVisible);
            Assert.Equal(emptyPanelHeight, panel.Bounds.Height, precision: 1);
            Click(window, close);
            window.UpdateLayout();
            Assert.False(panel.IsVisible);
        }
        finally { window.Close(); }
    }
}
