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
using static pdf_fluent.Tests.PdfTestHelpers;

namespace pdf_fluent.Tests;

public sealed class MainWindowTests
{
    [AvaloniaFact]
    public void TabsKeepThreeDistinctEdgeLevelsAndDoNotChangeOnSelectedHover()
    {
        using var vm = new MainViewModel();
        using var service = new FakePdfDocumentService(1);
        var document = new DocumentTabViewModel("a-long-document-title.pdf", service);
        vm.OpenDocuments.Add(document);
        vm.SelectedDocument = document;
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            window.UpdateLayout();
            var tab = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "TabSelectButton");
            var chrome = tab.GetVisualDescendants().OfType<pdf_fluent.Controls.EdgeTabChrome>().Single();
            var presenter = tab.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().Single(p => p.Name == "PART_ContentPresenter");
            var content = window.FindControl<Grid>("WorkspaceContent")!;
            Assert.True(chrome.IsActive);
            Assert.False(chrome.IsHitTestVisible);
            Assert.Equal(Colors.White, Assert.IsAssignableFrom<ISolidColorBrush>(chrome.Fill).Color);
            Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background).Color);
            Assert.Equal(40, tab.Bounds.Height);
            Assert.Equal(content.TranslatePoint(default, window)!.Value.Y,
                tab.TranslatePoint(new Point(0, tab.Bounds.Height), window)!.Value.Y);

            // The selected Edge tab keeps its white surface while the pointer is over it.
            window.MouseMove(tab.TranslatePoint(new Point(40, 20), window)!.Value);
            Assert.Equal(Colors.White, Assert.IsAssignableFrom<ISolidColorBrush>(chrome.Fill).Color);
            Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background).Color);

            // Inactive tabs use a clearly darker hover level than the strip, while the
            // selected level stays white. All three levels must be distinct.
            vm.ShowHomeCommand.Execute(null);
            window.UpdateLayout();
            Assert.False(chrome.IsActive);
            Color strip = Color.Parse("#EEF1F7");
            Color hover = Color.Parse("#DDE3ED");
            Assert.Equal(3, new[] { strip, hover, Colors.White }.Distinct().Count());
            window.MouseMove(new Point(10, 10));
            Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(chrome.Fill).Color);
            window.MouseMove(tab.TranslatePoint(new Point(40, 20), window)!.Value);
            Assert.Equal(hover, Assert.IsAssignableFrom<ISolidColorBrush>(chrome.Fill).Color);
            Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background).Color);

            var workspaceTab = window.FindControl<Avalonia.Controls.Primitives.ToggleButton>("WorkspaceTopTab")!;
            var workspaceChrome = workspaceTab.GetVisualDescendants().OfType<pdf_fluent.Controls.EdgeTabChrome>().Single();
            Assert.True(workspaceChrome.IsActive);
            window.MouseMove(workspaceTab.TranslatePoint(
                new Point(workspaceTab.Bounds.Width / 2, workspaceTab.Bounds.Height / 2), window)!.Value);
            Assert.Equal(Colors.White, Assert.IsAssignableFrom<ISolidColorBrush>(workspaceChrome.Fill).Color);
            var home = window.GetVisualDescendants().OfType<HomeView>().Single();
            Assert.Equal(Colors.White, Assert.IsAssignableFrom<ISolidColorBrush>(home.Background).Color);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void WorkspaceTabMorphsInsteadOfAddingFavoritesAndSettingsTabs()
    {
        using var viewModel = new MainViewModel();
        var window = new MainWindow { DataContext = viewModel };
        try
        {
            window.Show();
            window.UpdateLayout();

            var workspaceTab = window.FindControl<Avalonia.Controls.Primitives.ToggleButton>("WorkspaceTopTab")!;
            var label = workspaceTab.GetVisualDescendants().OfType<TextBlock>()
                .Single(text => text.Text is "主页" or "收藏" or "设置");
            var icon = workspaceTab.GetVisualDescendants().OfType<Optris.Icons.Avalonia.Icon>().Single();
            Assert.Equal("主页", label.Text);
            Assert.Equal("fa-solid fa-house", icon.Value);
            Assert.True(workspaceTab.IsChecked);

            // One fixed tab only: favorites/settings must not append new tabs.
            Assert.Single(workspaceTab.GetVisualParent()!.GetVisualDescendants()
                .OfType<Avalonia.Controls.Primitives.ToggleButton>());

            viewModel.ShowSettingsCommand.Execute(null);
            window.UpdateLayout();
            Assert.Equal("设置", label.Text);
            Assert.Equal("fa-solid fa-gear", icon.Value);
            Assert.True(workspaceTab.IsChecked);
            Assert.Single(workspaceTab.GetVisualParent()!.GetVisualDescendants()
                .OfType<Avalonia.Controls.Primitives.ToggleButton>());

            viewModel.ShowFavoritesCommand.Execute(null);
            window.UpdateLayout();
            Assert.Equal("收藏", label.Text);
            Assert.Equal("fa-solid fa-star", icon.Value);
            Assert.True(workspaceTab.IsChecked);
            Assert.Single(workspaceTab.GetVisualParent()!.GetVisualDescendants()
                .OfType<Avalonia.Controls.Primitives.ToggleButton>());

            viewModel.ShowHomeCommand.Execute(null);
            window.UpdateLayout();
            Assert.Equal("主页", label.Text);
            Assert.Equal("fa-solid fa-house", icon.Value);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ClickingTheActiveWorkspaceTabDoesNothingAndKeepsTheBindingAlive()
    {
        using var viewModel = new MainViewModel();
        using var service = new FakePdfDocumentService(1);
        var document = new DocumentTabViewModel("document.pdf", service);
        viewModel.OpenDocuments.Add(document);
        var window = new MainWindow { DataContext = viewModel };
        try
        {
            window.Show();
            window.UpdateLayout();
            viewModel.ShowSettingsCommand.Execute(null);
            window.UpdateLayout();
            var workspaceTab = window.FindControl<Avalonia.Controls.Primitives.ToggleButton>("WorkspaceTopTab")!;
            Assert.True(workspaceTab.IsChecked);

            Click(window, workspaceTab);
            window.UpdateLayout();

            Assert.True(viewModel.IsSettingsSelected);
            Assert.False(viewModel.IsHomeSelected);
            Assert.True(workspaceTab.IsChecked);
            Assert.True(workspaceTab.GetVisualDescendants()
                .OfType<pdf_fluent.Controls.EdgeTabChrome>().Single().IsActive);

            viewModel.SelectedDocument = document;
            window.UpdateLayout();
            Assert.False(workspaceTab.IsChecked);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ClickingTheInactiveWorkspaceTabFromADocumentReturnsHome()
    {
        using var viewModel = new MainViewModel();
        using var service = new FakePdfDocumentService(1);
        var document = new DocumentTabViewModel("document.pdf", service);
        viewModel.OpenDocuments.Add(document);
        viewModel.SelectedDocument = document;
        var window = new MainWindow { DataContext = viewModel };
        try
        {
            window.Show();
            window.UpdateLayout();
            var workspaceTab = window.FindControl<Avalonia.Controls.Primitives.ToggleButton>("WorkspaceTopTab")!;
            Assert.False(workspaceTab.IsChecked);

            Click(window, workspaceTab);
            window.UpdateLayout();

            Assert.True(viewModel.IsHomeSelected);
            Assert.Null(viewModel.SelectedDocument);
            Assert.True(workspaceTab.IsChecked);
        }
        finally { window.Close(); }
    }

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
                Color.Parse("#DDE3ED"),
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
            var tabChrome = tabButton.GetVisualDescendants().OfType<pdf_fluent.Controls.EdgeTabChrome>().Single();
            Assert.True(tabChrome.IsActive);
            Assert.False(tabChrome.IsHitTestVisible);
            Assert.True(tabButton.GetVisualAncestors().OfType<Border>().First().ClipToBounds);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SettingsNavigationOpensTheDedicatedPageAndReturnsHome()
    {
        using var viewModel = new MainViewModel();
        var window = new MainWindow { DataContext = viewModel };
        try
        {
            window.Show();
            window.UpdateLayout();

            Button settings = window.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Name == "SettingsNavButton");
            settings.Command!.Execute(null);
            window.UpdateLayout();

            Assert.True(viewModel.IsSettingsSelected);
            Assert.True(viewModel.IsSettingsOpen);
            Assert.False(viewModel.IsLibraryOpen);
            Assert.True(Assert.IsType<Avalonia.Controls.Primitives.ToggleButton>(settings).IsChecked);
            Assert.Contains("selected", settings.Classes);
            Assert.Single(window.GetVisualDescendants().OfType<SettingsView>(), view => view.IsVisible);
            Assert.True(window.GetVisualDescendants().OfType<Border>()
                .Single(border => border.Name == "SettingsSelectionIndicator").IsVisible);
            var workspaceTab = window.FindControl<Avalonia.Controls.Primitives.ToggleButton>("WorkspaceTopTab")!;
            Assert.True(workspaceTab.IsVisible);
            Assert.True(workspaceTab.IsChecked);
            Assert.Contains(workspaceTab.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "设置");
            Assert.False(window.GetVisualDescendants().OfType<HomeView>().Single().IsVisible);

            viewModel.ShowHomeCommand.Execute(null);
            window.UpdateLayout();
            Assert.False(viewModel.IsSettingsSelected);
            Assert.True(viewModel.IsHomeSelected);
            Assert.False(Assert.IsType<Avalonia.Controls.Primitives.ToggleButton>(settings).IsChecked);
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

            var flyout = Assert.IsAssignableFrom<MenuFlyout>(moreButton.Flyout);
            Assert.True(flyout.IsOpen);
            MenuItem[] items = flyout.Items.OfType<MenuItem>().ToArray();
            Assert.Equal(new[] { "打开新文件", "关闭所有标签页" }, items.Select(item => item.Header));
            MenuFlyoutPresenter presenter = items[0].GetVisualAncestors()
                .OfType<MenuFlyoutPresenter>()
                .Single();
            Assert.Equal(Colors.White, Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background).Color);
            Assert.Equal(new Thickness(0, 4), presenter.Padding);
            Assert.Equal(new CornerRadius(12), presenter.CornerRadius);
            Assert.Equal(-1, presenter.SelectedIndex);
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
    public async Task CtrlSSavesHighlightsAndCloseDoesNotAskAgain()
    {
        string filePath = Path.Combine(
            Path.GetTempPath(),
            $"pdf_fluent-tests-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(filePath, []);
        using var viewModel = new MainViewModel();
        using var service = new FakePdfDocumentService(1);
        using var document = new DocumentTabViewModel(filePath, service);
        await document.LoadAsync();
        await document.AddWordHighlightAsync(0, 1);
        viewModel.OpenDocuments.Add(document);
        viewModel.SelectedDocument = document;
        bool confirmationCalled = false;
        viewModel.DocumentCloseConfirmation = _ =>
        {
            confirmationCalled = true;
            return Task.FromResult(DocumentCloseDecision.Cancel);
        };
        var window = new MainWindow { DataContext = viewModel };
        try
        {
            window.Show();
            window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
            await PdfTestHelpers.WaitUntilAsync(() =>
                service.SaveCallCount == 1 && !document.HasUnsavedHighlights);

            Assert.Null(document.HighlightErrorMessage);
            Assert.Equal(1, service.SaveCallCount);
            await viewModel.CloseDocumentCommand.ExecuteAsync(document);
            Assert.False(confirmationCalled);
            Assert.DoesNotContain(document, viewModel.OpenDocuments);
        }
        finally
        {
            if (window.IsVisible) window.Close();
            if (File.Exists(filePath)) File.Delete(filePath);
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

    [AvaloniaFact]
    public async Task DroppingExternalFilesOpensEveryPdfAndIgnoresOtherFiles()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pdf_fluent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string firstPath = Path.Combine(directory, "first.pdf");
        string secondPath = Path.Combine(directory, "second.PDF");
        string textPath = Path.Combine(directory, "notes.txt");
        File.Copy(PdfTestHelpers.SamplePdfPath(), firstPath);
        File.Copy(PdfTestHelpers.SamplePdfPath(), secondPath);
        File.WriteAllText(textPath, "Not a PDF");
        using var viewModel = new MainViewModel(
            new PDFiumService(),
            new RecentDocumentsService(Path.Combine(directory, "recent.json")),
            new FavoriteDocumentsService(Path.Combine(directory, "favorites.json")));
        var window = new MainWindow { DataContext = viewModel };
        try
        {
            window.Show();
            window.UpdateLayout();
            Point dropPoint = new(window.Bounds.Width / 2, window.Bounds.Height / 2);

            DataTransfer textOnly = await CreateFileTransferAsync(window, textPath);
            window.DragDrop(
                dropPoint, Avalonia.Input.Raw.RawDragEventType.DragOver,
                textOnly, DragDropEffects.Copy, RawInputModifiers.None);
            window.DragDrop(
                dropPoint, Avalonia.Input.Raw.RawDragEventType.Drop,
                textOnly, DragDropEffects.Copy, RawInputModifiers.None);
            Assert.Empty(viewModel.OpenDocuments);

            DataTransfer files = await CreateFileTransferAsync(window, firstPath, textPath, secondPath);
            window.DragDrop(
                dropPoint, Avalonia.Input.Raw.RawDragEventType.DragOver,
                files, DragDropEffects.Copy, RawInputModifiers.None);
            window.DragDrop(
                dropPoint, Avalonia.Input.Raw.RawDragEventType.Drop,
                files, DragDropEffects.Copy, RawInputModifiers.None);
            await PdfTestHelpers.WaitUntilAsync(
                () => !viewModel.IsOpening && viewModel.OpenDocuments.Count == 2 &&
                      ReferenceEquals(viewModel.SelectedDocument, viewModel.OpenDocuments[1]) &&
                      viewModel.SelectedDocument.IsLoaded,
                TimeSpan.FromSeconds(15));

            Assert.Equal(new[] { firstPath, secondPath }, viewModel.OpenDocuments.Select(document => document.FilePath));
            Assert.All(viewModel.OpenDocuments, document => Assert.Null(document.ErrorMessage));
            Assert.Equal(2, viewModel.RecentDocuments.Count);
            Assert.Equal(secondPath, viewModel.SelectedDocument!.FilePath);

            DataTransfer duplicate = await CreateFileTransferAsync(window, firstPath);
            window.DragDrop(
                dropPoint, Avalonia.Input.Raw.RawDragEventType.DragOver,
                duplicate, DragDropEffects.Copy, RawInputModifiers.None);
            window.DragDrop(
                dropPoint, Avalonia.Input.Raw.RawDragEventType.Drop,
                duplicate, DragDropEffects.Copy, RawInputModifiers.None);

            Assert.Equal(2, viewModel.OpenDocuments.Count);
            Assert.Equal(firstPath, viewModel.SelectedDocument.FilePath);
        }
        finally
        {
            if (window.IsVisible) window.Close();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<DataTransfer> CreateFileTransferAsync(MainWindow window, params string[] paths)
    {
        var data = new DataTransfer();
        foreach (string path in paths)
        {
            var file = await window.StorageProvider.TryGetFileFromPathAsync(new Uri(Path.GetFullPath(path)));
            Assert.NotNull(file);
            data.Add(DataTransferItem.CreateFile(file));
        }
        return data;
    }
}
