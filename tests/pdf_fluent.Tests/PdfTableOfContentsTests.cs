using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using pdf_fluent.Models;
using pdf_fluent.ViewModels;
using pdf_fluent.Views;
using Xunit;
using static pdf_fluent.Tests.PdfTestHelpers;
using Path = Avalonia.Controls.Shapes.Path;

namespace pdf_fluent.Tests;

public sealed class PdfTableOfContentsTests
{
    [AvaloniaFact]
    public async Task TableOfContentsStartsAtFirstLevelAndExpandsEveryDepth()
    {
        using var service = new FakePdfDocumentService(4);
        service.Bookmarks = CreateBookmarks();
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();

        Assert.True(document.HasTableOfContents);
        Assert.Equal(new[] { "Chapter 1", "Chapter 2" }, Titles(document));

        TocEntryViewModel chapter = document.VisibleTocEntries[0];
        chapter.IsExpanded = true;

        Assert.Equal(new[] { "Chapter 1", "Section 1.1", "Chapter 2" }, Titles(document));
        TocEntryViewModel section = document.VisibleTocEntries[1];
        Assert.Equal(1, section.Depth);
        Assert.Equal(new Thickness(18, 0, 0, 0), section.Indent);

        section.IsExpanded = true;

        Assert.Equal(
            new[] { "Chapter 1", "Section 1.1", "Detail 1.1.1", "Chapter 2" },
            Titles(document));
        Assert.Equal(new Thickness(36, 0, 0, 0), document.VisibleTocEntries[2].Indent);

        chapter.IsExpanded = false;
        Assert.Equal(new[] { "Chapter 1", "Chapter 2" }, Titles(document));
    }

    [AvaloniaFact]
    public async Task TableOfContentsButtonTogglesAnimatedFlyoutAndCheckedBrush()
    {
        using var service = new FakePdfDocumentService(4);
        service.Bookmarks = CreateBookmarks();
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1200, Height = 900, Content = view };

        try
        {
            window.Show();
            window.UpdateLayout();

            ToggleButton button = Named<ToggleButton>(view, "TableOfContentsButton");
            Grid flyout = Named<Grid>(view, "TableOfContentsOverlay");
            Border toolbar = Named<Border>(view, "ReaderToolbar");
            var viewport = Named<Grid>(view, "DocumentViewport");
            Point viewportOrigin = viewport.TranslatePoint(default, view)!.Value;
            double viewportWidth = viewport.Bounds.Width;
            Assert.True(button.IsEnabled);
            Assert.False(button.IsChecked);
            Assert.False(view.IsTableOfContentsVisible);
            Assert.Equal(new CornerRadius(8), button.CornerRadius);
            Assert.Equal(34, button.Bounds.Width);
            Assert.Equal(button.Bounds.Height, toolbar.Bounds.Height);
            Assert.Equal(
                Colors.Transparent,
                Assert.IsAssignableFrom<ISolidColorBrush>(button.Background).Color);

            window.MouseMove(Center(button, window));
            Assert.Equal(
                Color.Parse("#F0F2F8"),
                Assert.IsAssignableFrom<ISolidColorBrush>(button.Background).Color);
            ContentPresenter hoverSurface = button.GetVisualChildren()
                .OfType<ContentPresenter>()
                .Single();
            Assert.Equal(
                Color.Parse("#F0F2F8"),
                Assert.IsAssignableFrom<ISolidColorBrush>(hoverSurface.Background).Color);

            Click(window, button);
            window.UpdateLayout();

            Assert.True(document.IsTableOfContentsOpen);
            Assert.True(button.IsChecked);
            Assert.True(view.IsTableOfContentsVisible);
            Assert.Contains("open", flyout.Classes);
            Assert.Equal(viewportOrigin, viewport.TranslatePoint(default, view)!.Value);
            Assert.Equal(viewportWidth, viewport.Bounds.Width);
            Assert.Equal(
                Color.Parse("#E8E3FF"),
                Assert.IsAssignableFrom<ISolidColorBrush>(button.Background).Color);
            ContentPresenter buttonSurface = button.GetVisualChildren()
                .OfType<ContentPresenter>()
                .Single();
            Assert.Equal(
                Color.Parse("#E8E3FF"),
                Assert.IsAssignableFrom<ISolidColorBrush>(buttonSurface.Background).Color);
            Assert.Equal(
                Color.Parse("#6C5CE7"),
                Assert.IsAssignableFrom<ISolidColorBrush>(button.Foreground).Color);

            window.MouseMove(new Point(500, 500));
            Assert.Equal(
                Color.Parse("#E8E3FF"),
                Assert.IsAssignableFrom<ISolidColorBrush>(button.Background).Color);

            await Task.Delay(240);
            window.UpdateLayout();

            Assert.Equal(1, flyout.Opacity);
            Assert.Equal(new Thickness(0, 48, 0, 20), flyout.Margin);
            Assert.Equal(2, flyout.Transitions?.Count);
            Assert.Equal(
                TimeSpan.FromMilliseconds(180),
                Assert.IsType<DoubleTransition>(flyout.Transitions![0]).Duration);
            Assert.Equal(
                TimeSpan.FromMilliseconds(180),
                Assert.IsType<ThicknessTransition>(flyout.Transitions[1]).Duration);
            Assert.Equal(360, flyout.MaxWidth);
            Assert.Equal(0, flyout.TranslatePoint(new Point(0, 0), view)!.Value.X, precision: 1);

            Path pointer = Named<Path>(view, "TableOfContentsPointer");
            Path pointerFill = Named<Path>(view, "TableOfContentsPointerFill");
            Border panel = Named<Border>(view, "TableOfContentsPanel");
            Assert.Equal(Avalonia.Layout.VerticalAlignment.Stretch, flyout.VerticalAlignment);
            Assert.Equal(view.Bounds.Height - 68, flyout.Bounds.Height, precision: 1);
            double panelBottom = panel.TranslatePoint(
                new Point(0, panel.Bounds.Height), view)!.Value.Y;
            Assert.Equal(view.Bounds.Height - 20, panelBottom, precision: 1);
            Assert.Equal(
                Center(button, window).X,
                Center(pointer, window).X,
                precision: 1);
            Assert.True(Center(pointer, window).Y > button.Bounds.Bottom);
            Assert.Equal(
                Colors.White,
                Assert.IsAssignableFrom<ISolidColorBrush>(pointerFill.Fill).Color);
            Point pointerStart = pointer.TranslatePoint(new Point(0, 8), view)!.Value;
            Point panelJoin = panel.TranslatePoint(new Point(23, 0), view)!.Value;
            Assert.Equal(panelJoin.X, pointerStart.X, precision: 1);
            Assert.Equal(panelJoin.Y, pointerStart.Y, precision: 1);
            Assert.True(pointer.Bounds.Bottom > panel.Bounds.Top);

            Click(window, button);
            Assert.False(document.IsTableOfContentsOpen);
            Assert.False(button.IsChecked);
            Assert.DoesNotContain("open", flyout.Classes);
            await Task.Delay(240);
            window.UpdateLayout();

            Assert.False(view.IsTableOfContentsVisible);
            window.MouseMove(new Point(500, 500));
            Assert.Equal(
                Colors.Transparent,
                Assert.IsAssignableFrom<ISolidColorBrush>(button.Background).Color);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ExpanderAndTitleHaveSeparateHitAreasAndTitleNavigates()
    {
        using var service = new FakePdfDocumentService(4);
        service.Bookmarks = CreateBookmarks();
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1200, Height = 900, Content = view };

        try
        {
            window.Show();
            window.UpdateLayout();
            Click(window, Named<ToggleButton>(view, "TableOfContentsButton"));
            window.UpdateLayout();

            TocEntryViewModel chapter = document.VisibleTocEntries[0];
            ToggleButton chapterExpander = ExpanderFor(view, chapter);
            Button chapterTitle = TitleButtonFor(view, chapter);
            Rect chapterExpanderBounds = RelativeBounds(chapterExpander, view);
            Rect chapterTitleBounds = RelativeBounds(chapterTitle, view);
            Assert.True(chapterTitleBounds.Left >= chapterExpanderBounds.Right);

            Click(window, chapterExpander);
            window.UpdateLayout();

            Assert.True(chapter.IsExpanded);
            Assert.Equal(1, document.CurrentPage);
            chapterExpander = ExpanderFor(view, chapter);
            AssertRotation(chapterExpander, 90);
            Assert.Equal(
                Colors.Transparent,
                Assert.IsAssignableFrom<ISolidColorBrush>(chapterExpander.Background).Color);
            ContentPresenter expandedSurface = chapterExpander.GetVisualChildren()
                .OfType<ContentPresenter>()
                .Single();
            Assert.Equal(
                Colors.Transparent,
                Assert.IsAssignableFrom<ISolidColorBrush>(expandedSurface.Background).Color);

            Click(window, chapterExpander);
            window.UpdateLayout();
            Assert.False(chapter.IsExpanded);
            Assert.Equal(1, document.CurrentPage);
            chapterExpander = ExpanderFor(view, chapter);
            AssertRotation(chapterExpander, 0);

            Click(window, chapterExpander);
            window.UpdateLayout();
            Assert.True(chapter.IsExpanded);
            TocEntryViewModel section = document.VisibleTocEntries[1];
            Button sectionTitle = TitleButtonFor(view, section);
            double firstLevelTitleX = TitleButtonFor(view, chapter)
                .TranslatePoint(new Point(0, 0), view)!.Value.X;
            double secondLevelTitleX = sectionTitle.TranslatePoint(new Point(0, 0), view)!.Value.X;
            Assert.Equal(18, secondLevelTitleX - firstLevelTitleX, precision: 1);

            Click(window, sectionTitle);
            window.UpdateLayout();
            Assert.Equal(2, document.CurrentPage);
            Assert.False(section.IsExpanded);

            ToggleButton sectionExpander = ExpanderFor(view, section);
            Click(window, sectionExpander);
            window.UpdateLayout();
            Assert.True(section.IsExpanded);
            Assert.Equal(2, document.CurrentPage);

            TocEntryViewModel detail = document.VisibleTocEntries[2];
            Button detailTitle = TitleButtonFor(view, detail);
            double thirdLevelTitleX = detailTitle.TranslatePoint(new Point(0, 0), view)!.Value.X;
            Assert.Equal(18, thirdLevelTitleX - secondLevelTitleX, precision: 1);

            Click(window, detailTitle);
            window.UpdateLayout();
            Assert.Equal(3, document.CurrentPage);
            Assert.Equal("3 / 4", document.PageIndicator);
            Assert.True(document.IsTableOfContentsOpen);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TocItemsUseNaturalLineHeightAndAlignExpanderToTheFirstLine()
    {
        using var service = new FakePdfDocumentService(3);
        var longEntry = new TocItem
        {
            Title = "这是一个专门用于验证目录标题自动换行高度的双行目录项",
            PageIndex = 1,
        };
        longEntry.Children.Add(new TocItem { Title = "Child", PageIndex = 2 });
        service.Bookmarks =
        [
            new TocItem { Title = "Short", PageIndex = 0 },
            longEntry,
        ];
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1200, Height = 900, Content = view };

        try
        {
            window.Show();
            window.UpdateLayout();
            Click(window, Named<ToggleButton>(view, "TableOfContentsButton"));
            window.UpdateLayout();

            Button singleLine = TitleButtonFor(view, document.VisibleTocEntries[0]);
            Button multiLine = TitleButtonFor(view, document.VisibleTocEntries[1]);
            var singleText = Assert.IsType<TextBlock>(singleLine.Content);
            var multiText = Assert.IsType<TextBlock>(multiLine.Content);

            Assert.Equal(18, singleText.Bounds.Height, precision: 1);
            Assert.Equal(36, multiText.Bounds.Height, precision: 1);
            Assert.Equal(singleLine.Bounds.Height + 18, multiLine.Bounds.Height, precision: 1);
            foreach (Button item in new[] { singleLine, multiLine })
            {
                Assert.Equal(new Thickness(7, 8), item.Padding);
                Assert.Equal(new Thickness(0, 1), item.Margin);
            }

            ToggleButton expander = ExpanderFor(view, document.VisibleTocEntries[1]);
            double firstLineCenter = multiText.TranslatePoint(
                new Point(0, multiText.LineHeight / 2), view)!.Value.Y;
            Assert.Equal(Avalonia.Layout.VerticalAlignment.Top, expander.VerticalAlignment);
            Assert.Equal(Center(expander, view).Y, firstLineCenter, precision: 1);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task DocumentWithoutBookmarksOpensEmptyTableOfContents()
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

            ToggleButton button = Named<ToggleButton>(view, "TableOfContentsButton");
            Assert.False(document.HasTableOfContents);
            Assert.True(button.IsEnabled);
            var icon = Assert.IsAssignableFrom<Optris.Icons.Avalonia.Icon>(button.Content);
            Assert.Equal(
                Color.Parse("#666D80"),
                Assert.IsAssignableFrom<ISolidColorBrush>(icon.Foreground).Color);
            Assert.Equal(
                Colors.Transparent,
                Assert.IsAssignableFrom<ISolidColorBrush>(button.Background).Color);

            Click(window, button);
            window.UpdateLayout();

            Assert.True(document.IsTableOfContentsOpen);
            Assert.True(button.IsChecked);
            Assert.True(view.IsTableOfContentsVisible);
            Grid flyout = Named<Grid>(view, "TableOfContentsOverlay");
            Assert.Contains("open", flyout.Classes);
            ItemsControl items = Named<ItemsControl>(view, "TocItems");
            Assert.Empty(items.Items);
            Assert.Empty(document.VisibleTocEntries);
            Assert.DoesNotContain(
                view.GetVisualDescendants().OfType<Button>(),
                item => item.Name == "TocNavigateButton");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task RealPdfBookmarksLoadAsFirstLevelEntries()
    {
        using var service = new pdf_fluent.Services.PDFiumService();
        using var document = new DocumentTabViewModel(PdfTestHelpers.SamplePdfPath(), service);
        await document.LoadAsync();

        Assert.True(document.HasTableOfContents);
        Assert.NotEmpty(document.VisibleTocEntries);
        Assert.All(document.VisibleTocEntries, entry => Assert.Equal(0, entry.Depth));
        Assert.Contains(document.VisibleTocEntries, entry => entry.CanNavigate);
    }

    private static List<TocItem> CreateBookmarks()
    {
        var detail = new TocItem { Title = "Detail 1.1.1", PageIndex = 2 };
        var section = new TocItem { Title = "Section 1.1", PageIndex = 1 };
        section.Children.Add(detail);
        var chapter = new TocItem { Title = "Chapter 1", PageIndex = 3 };
        chapter.Children.Add(section);
        return
        [
            chapter,
            new TocItem { Title = "Chapter 2", PageIndex = 0 },
        ];
    }

    private static string[] Titles(DocumentTabViewModel document) =>
        document.VisibleTocEntries.Select(entry => entry.Title).ToArray();

    private static ToggleButton ExpanderFor(Visual root, TocEntryViewModel entry) =>
        root.GetVisualDescendants().OfType<ToggleButton>().Single(button =>
            button.Name == "TocExpanderButton" && ReferenceEquals(button.DataContext, entry));

    private static Button TitleButtonFor(Visual root, TocEntryViewModel entry) =>
        root.GetVisualDescendants().OfType<Button>().Single(button =>
            button.Name == "TocNavigateButton" && ReferenceEquals(button.DataContext, entry));

    private static Rect RelativeBounds(Control control, Visual relativeTo)
    {
        Point topLeft = control.TranslatePoint(new Point(0, 0), relativeTo)!.Value;
        return new Rect(topLeft, control.Bounds.Size);
    }

    private static void AssertRotation(Control control, double angle)
    {
        Matrix expected = new RotateTransform(angle).Value;
        Matrix actual = Assert.IsAssignableFrom<ITransform>(control.RenderTransform).Value;
        Assert.Equal(expected.M11, actual.M11, precision: 6);
        Assert.Equal(expected.M12, actual.M12, precision: 6);
        Assert.Equal(expected.M21, actual.M21, precision: 6);
        Assert.Equal(expected.M22, actual.M22, precision: 6);
    }
}
