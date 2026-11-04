using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using pdf_fluent.Models;
using pdf_fluent.Services;
using pdf_fluent.ViewModels;
using pdf_fluent.Views;
using Xunit;

namespace pdf_fluent.Tests;

public sealed class MainWindowTests
{
    [AvaloniaFact]
    public void WholeTabSelectsDocumentExceptForTheIndependentCloseButton()
    {
        using var viewModel = new MainViewModel();
        using var firstService = new FakePdfDocumentService(1);
        using var secondService = new FakePdfDocumentService(1);
        var first = new DocumentTabViewModel("first.pdf", firstService);
        var second = new DocumentTabViewModel("second.pdf", secondService);
        viewModel.OpenDocuments.Add(first);
        viewModel.OpenDocuments.Add(second);
        viewModel.SelectedDocument = first;

        var window = new MainWindow { DataContext = viewModel };
        try
        {
            window.Show();
            viewModel.ShowHomeCommand.Execute(null);
            window.UpdateLayout();

            Button navButton = window.GetVisualDescendants()
                .OfType<Button>()
                .First(button => button.Classes.Contains("nav-button") && button.IsEnabled);
            Assert.Equal("HomeNavButton", navButton.Name);
            Assert.True(Assert.IsType<Avalonia.Controls.Primitives.ToggleButton>(navButton).IsChecked);
            Assert.Contains("selected", navButton.Classes);
            Point navCenter = navButton.TranslatePoint(
                new Point(navButton.Bounds.Width / 2, navButton.Bounds.Height / 2), window)!.Value;
            window.MouseMove(navCenter);
            Assert.Equal(
                Color.Parse("#ECEEFF"),
                Assert.IsAssignableFrom<ISolidColorBrush>(navButton.Background).Color);
            Assert.Equal(
                Color.Parse("#625BE7"),
                Assert.IsAssignableFrom<ISolidColorBrush>(navButton.Foreground).Color);
            Avalonia.Controls.Presenters.ContentPresenter navSurface = navButton.GetVisualChildren()
                .OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                .Single();
            Assert.Equal(
                Color.Parse("#ECEEFF"),
                Assert.IsAssignableFrom<ISolidColorBrush>(navSurface.Background).Color);

            Button tabButton = window.GetVisualDescendants()
                .OfType<Button>()
                .Single(button => button.Name == "TabSelectButton" && ReferenceEquals(button.DataContext, second));
            Button closeButton = tabButton.GetVisualParent()!.GetVisualDescendants()
                .OfType<Button>()
                .Single(button => button.Classes.Contains("tab-close"));
            Point point = tabButton.TranslatePoint(
                new Point(100, tabButton.Bounds.Height - 1.5), window)!.Value;
            Point hoverPoint = tabButton.TranslatePoint(new Point(30, 20), window)!.Value;

            window.MouseMove(hoverPoint);
            Assert.Equal(
                Color.Parse("#F3F4F9"),
                Assert.IsAssignableFrom<ISolidColorBrush>(tabButton.Background).Color);

            Point closeCenter = closeButton.TranslatePoint(
                new Point(closeButton.Bounds.Width / 2, closeButton.Bounds.Height / 2), window)!.Value;
            window.MouseMove(closeCenter);
            Assert.Equal(
                Color.Parse("#E9EBF2"),
                Assert.IsAssignableFrom<ISolidColorBrush>(closeButton.Background).Color);
            Avalonia.Controls.Presenters.ContentPresenter closeSurface = closeButton.GetVisualChildren()
                .OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                .Single();
            Assert.Equal(
                Color.Parse("#E9EBF2"),
                Assert.IsAssignableFrom<ISolidColorBrush>(closeSurface.Background).Color);

            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);

            Assert.Same(second, viewModel.SelectedDocument);
            Assert.Equal(2, viewModel.OpenDocuments.Count);
            Assert.Equal(40, tabButton.Bounds.Height, precision: 1);
            Assert.True(tabButton.Bounds.Height > closeButton.Bounds.Height);
            Assert.Equal(new CornerRadius(10), tabButton.CornerRadius);
            Border hoverSurface = tabButton.GetVisualDescendants().OfType<Border>().First();
            Assert.Equal(new CornerRadius(10), hoverSurface.CornerRadius);
            Assert.True(hoverSurface.ClipToBounds);
            Assert.True(tabButton.GetVisualAncestors().OfType<Border>().First().ClipToBounds);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DisabledSettingsButtonUsesLightGrayIconWithoutADisabledBackground()
    {
        using var viewModel = new MainViewModel();
        var window = new MainWindow { DataContext = viewModel };
        try
        {
            window.Show();
            window.UpdateLayout();

            Button settings = window.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Classes.Contains("nav-button") && !button.IsEnabled);
            Assert.Equal(1, settings.Opacity);
            Assert.Equal(
                Color.Parse("#C4C8D2"),
                Assert.IsAssignableFrom<ISolidColorBrush>(settings.Foreground).Color);
            Assert.Equal(
                Colors.Transparent,
                Assert.IsAssignableFrom<ISolidColorBrush>(settings.Background).Color);
            var icon = Assert.IsAssignableFrom<Optris.Icons.Avalonia.Icon>(settings.Content);
            Assert.Equal(
                Color.Parse("#C4C8D2"),
                Assert.IsAssignableFrom<ISolidColorBrush>(icon.Foreground).Color);

            Point center = settings.TranslatePoint(
                new Point(settings.Bounds.Width / 2, settings.Bounds.Height / 2), window)!.Value;
            window.MouseMove(center);
            Avalonia.Controls.Presenters.ContentPresenter surface = settings.GetVisualChildren()
                .OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                .Single();
            Assert.Equal(
                Colors.Transparent,
                Assert.IsAssignableFrom<ISolidColorBrush>(surface.Background).Color);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TopRightMenuOpensAndExposesOpenAndCloseAllCommands()
    {
        using var viewModel = new MainViewModel();
        using var service = new FakePdfDocumentService(1);
        viewModel.OpenDocuments.Add(new DocumentTabViewModel("document.pdf", service));
        var window = new MainWindow { DataContext = viewModel };
        bool pickerCalled = false;
        viewModel.PdfFilePicker = () =>
        {
            pickerCalled = true;
            return Task.FromResult<string?>(null);
        };
        try
        {
            window.Show();
            window.UpdateLayout();

            Button moreButton = window.GetVisualDescendants()
                .OfType<Button>()
                .Single(button => button.Name == "MoreActionsButton");
            Point buttonCenter = moreButton.TranslatePoint(
                new Point(moreButton.Bounds.Width / 2, moreButton.Bounds.Height / 2), window)!.Value;
            Assert.True(buttonCenter.X > window.Bounds.Width / 2);
            Assert.InRange(buttonCenter.Y, 0, 56);

            window.MouseMove(buttonCenter);
            Assert.Equal(
                Color.Parse("#F0F3F9"),
                Assert.IsAssignableFrom<ISolidColorBrush>(moreButton.Background).Color);
            Avalonia.Controls.Presenters.ContentPresenter moreSurface = moreButton.GetVisualChildren()
                .OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                .Single();
            Assert.Equal(
                Color.Parse("#F0F3F9"),
                Assert.IsAssignableFrom<ISolidColorBrush>(moreSurface.Background).Color);

            window.MouseDown(buttonCenter, MouseButton.Left);
            window.MouseUp(buttonCenter, MouseButton.Left);
            window.UpdateLayout();

            var flyout = Assert.IsType<MenuFlyout>(moreButton.Flyout);
            Assert.True(flyout.IsOpen);
            MenuItem[] items = flyout.Items.OfType<MenuItem>().ToArray();
            Assert.Equal(new[] { "打开新文件", "关闭所有标签页" }, items.Select(item => item.Header));
            MenuFlyoutPresenter presenter = items[0].GetVisualAncestors()
                .OfType<MenuFlyoutPresenter>()
                .Single();
            Assert.Equal(Colors.White, Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background).Color);
            Assert.Equal(new Thickness(0, 4), presenter.Padding);
            Assert.Equal(new CornerRadius(12), presenter.CornerRadius);
            Assert.All(items, item =>
            {
                Assert.Equal(new Thickness(4, 0), item.Margin);
                Assert.Equal(new CornerRadius(8), item.CornerRadius);
                Assert.Equal(new Thickness(10, 9), item.Padding);
            });

            await Assert.IsAssignableFrom<IAsyncRelayCommand>(items[0].Command).ExecuteAsync(null);
            Assert.True(pickerCalled);

            items[1].Command!.Execute(null);
            Assert.Empty(viewModel.OpenDocuments);
            Assert.Null(viewModel.SelectedDocument);
            flyout.Hide();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void HomeIndicatorUsesTheNavigationButtonCenterAndFavoriteDotTracksTheCurrentPage()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pdf_fluent-tests", Guid.NewGuid().ToString("N"));
        using var viewModel = new MainViewModel(
            new PDFiumService(),
            new RecentDocumentsService(Path.Combine(directory, "recent.json")),
            new FavoriteDocumentsService(Path.Combine(directory, "favorites.json")));
        viewModel.FavoriteDocuments.Add(new RecentDocumentViewModel(new RecentDocument
        {
            FilePath = "favorite.pdf",
            PageCount = 8,
            LastOpened = DateTimeOffset.Now,
            IsUnread = true,
        }));
        var window = new MainWindow { DataContext = viewModel };
        try
        {
            window.Show();
            window.UpdateLayout();

            Border indicator = window.GetVisualDescendants().OfType<Border>()
                .Single(border => border.Name == "HomeSelectionIndicator");
            Button homeButton = window.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Name == "HomeNavButton");
            Border dot = window.GetVisualDescendants().OfType<Border>()
                .Single(border => border.Name == "FavoriteNotificationDot");

            Assert.True(indicator.IsVisible);
            Assert.True(dot.IsVisible);
            Assert.Equal(3, indicator.Bounds.Width, precision: 1);
            Assert.Equal(22, indicator.Bounds.Height, precision: 1);
            Point indicatorCenter = indicator.TranslatePoint(
                new Point(indicator.Bounds.Width / 2, indicator.Bounds.Height / 2), window)!.Value;
            Point homeCenter = homeButton.TranslatePoint(
                new Point(homeButton.Bounds.Width / 2, homeButton.Bounds.Height / 2), window)!.Value;
            Assert.Equal(homeCenter.Y, indicatorCenter.Y, precision: 1);
            Assert.True(indicatorCenter.X < homeCenter.X);
            double indicatorLeft = indicator.TranslatePoint(new Point(0, 0), window)!.Value.X;
            double indicatorRight = indicator.TranslatePoint(new Point(indicator.Bounds.Width, 0), window)!.Value.X;
            double homeLeft = homeButton.TranslatePoint(new Point(0, 0), window)!.Value.X;
            Assert.True(Math.Abs(indicatorLeft - (homeLeft - indicatorRight)) <= 1);

            viewModel.ShowFavoritesCommand.Execute(null);
            window.UpdateLayout();
            Assert.True(viewModel.IsFavoritesSelected);
            Assert.False(viewModel.IsHomeSelected);
            Assert.False(dot.IsVisible);
            Assert.False(indicator.IsVisible);

            viewModel.ShowHomeCommand.Execute(null);
            window.UpdateLayout();
            Assert.True(indicator.IsVisible);
            Assert.False(viewModel.HasFavoriteNotification);
            Assert.False(dot.IsVisible);
        }
        finally
        {
            window.Close();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public void NativeTitleBarIsUsedWithoutAnAdditionalInClientTitle()
    {
        using var viewModel = new MainViewModel();
        var window = new MainWindow { DataContext = viewModel };
        try
        {
            window.Show();
            window.UpdateLayout();

            Assert.Equal(WindowDecorations.Full, window.WindowDecorations);
            Assert.False(window.ExtendClientAreaToDecorationsHint);
            Assert.Equal("Fluent PDF", window.Title);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Grid>(), grid => grid.Name == "TitleBar");
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), button =>
                button.Name is "CloseWindowButton" or "MaximizeButton" or "MinimizeButton");
        }
        finally
        {
            window.Close();
        }
    }
}
