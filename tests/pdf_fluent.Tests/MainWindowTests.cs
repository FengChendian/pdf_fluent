using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
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
            window.UpdateLayout();

            Button navButton = window.GetVisualDescendants()
                .OfType<Button>()
                .First(button => button.Classes.Contains("nav-button") && button.IsEnabled);
            Point navCenter = navButton.TranslatePoint(
                new Point(navButton.Bounds.Width / 2, navButton.Bounds.Height / 2), window)!.Value;
            window.MouseMove(navCenter);
            Assert.Equal(
                Color.Parse("#F0F3F9"),
                Assert.IsAssignableFrom<ISolidColorBrush>(navButton.Background).Color);
            Avalonia.Controls.Presenters.ContentPresenter navSurface = navButton.GetVisualChildren()
                .OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                .Single();
            Assert.Equal(
                Color.Parse("#F0F3F9"),
                Assert.IsAssignableFrom<ISolidColorBrush>(navSurface.Background).Color);

            Button tabButton = window.GetVisualDescendants()
                .OfType<Button>()
                .Single(button => button.Name == "TabSelectButton" && ReferenceEquals(button.DataContext, second));
            Button closeButton = tabButton.GetVisualParent()!.GetVisualDescendants()
                .OfType<Button>()
                .Single(button => button.Classes.Contains("tab-close"));
            Point point = tabButton.TranslatePoint(
                new Point(100, tabButton.Bounds.Height - 1.5), window)!.Value;
            Point hoverPoint = tabButton.TranslatePoint(new Point(30, 22), window)!.Value;

            window.MouseMove(hoverPoint);
            Assert.Equal(
                Color.Parse("#F0F3F9"),
                Assert.IsAssignableFrom<ISolidColorBrush>(tabButton.Background).Color);

            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);

            Assert.Same(second, viewModel.SelectedDocument);
            Assert.Equal(2, viewModel.OpenDocuments.Count);
            Assert.Equal(44, tabButton.Bounds.Height, precision: 1);
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
            Assert.True(buttonCenter.Y < 60);

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
}
