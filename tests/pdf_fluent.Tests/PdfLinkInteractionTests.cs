using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using pdf_fluent.Controls;
using pdf_fluent.Models;
using pdf_fluent.Services;
using pdf_fluent.ViewModels;
using pdf_fluent.Views;
using Xunit;
using static pdf_fluent.Tests.PdfTestHelpers;

namespace pdf_fluent.Tests;

public sealed class PdfLinkInteractionTests
{
    [AvaloniaFact]
    public async Task HoverAndSingleClickOpenTheUriOnlyOnReleaseIncludingSmallPointerJitter()
    {
        using var reader = await Reader.CreateAsync();
        Point point = reader.At(25, 744);
        reader.Window.MouseMove(point);
        Assert.True(reader.View.IsLinkCursor);
        Assert.Equal("https://example.com/target", Named<TextBlock>(reader.View, "LinkStatusText").Text);
        Assert.True(Named<Border>(reader.View, "LinkStatus").IsVisible);
        reader.Window.MouseDown(point, MouseButton.Left);
        Assert.Empty(reader.Launched);
        reader.Window.MouseMove(point + new Vector(1, 0));
        reader.Window.MouseUp(point + new Vector(1, 0), MouseButton.Left);
        Assert.Equal("https://example.com/target", Assert.Single(reader.Launched).AbsoluteUri);
        Assert.False(reader.Document.HasSelection);
        reader.Window.MouseMove(reader.At(450, 600));
        Assert.False(reader.View.IsLinkCursor);
        Assert.False(Named<Border>(reader.View, "LinkStatus").IsVisible);
    }

    [AvaloniaFact]
    public async Task DraggingLinkTextPreservesExactCopyAndDoesNotActivateOnReturningToTheLink()
    {
        using var reader = await Reader.CreateAsync();
        Point start = reader.At(22, 744), end = reader.At(60, 744);
        reader.Window.MouseDown(start, MouseButton.Left);
        reader.Window.MouseMove(end);
        reader.Window.MouseUp(end, MouseButton.Left);
        Assert.Equal("PDF ", await reader.Document.GetSelectedTextAsync());
        Assert.Empty(reader.Launched);

        reader.Window.MouseDown(start, MouseButton.Left);
        reader.Window.MouseMove(reader.At(150, 744));
        reader.Window.MouseMove(start);
        reader.Window.MouseUp(start, MouseButton.Left);
        Assert.Empty(reader.Launched);
    }

    [AvaloniaFact]
    public async Task ImageLinkNavigatesToAnUnrealizedPageAndExactPositionAtDestinationZoom()
    {
        using var reader = await Reader.CreateAsync();
        Assert.DoesNotContain(reader.View.PageItems.GetRealizedContainers(), item =>
            item.DataContext is PdfPageViewModel { PageIndex: 25 });
        Point image = reader.At(240, 730);
        reader.Window.MouseMove(image);
        Assert.True(reader.View.IsLinkCursor);
        reader.Window.MouseDown(image, MouseButton.Left);
        Assert.Equal(1, reader.Document.CurrentPage);
        reader.Window.MouseUp(image, MouseButton.Left);
        reader.Window.UpdateLayout();
        Assert.Equal(2, reader.Document.Zoom);
        Assert.Equal(26, reader.Document.CurrentPage);
        PdfVirtualizingPanel panel = reader.View.PageItems.GetVisualDescendants().OfType<PdfVirtualizingPanel>().Single();
        double expectedY = panel.Padding.Top + panel.ItemTopAt(25, 2) + 200 * DocumentTabViewModel.LayoutScale * 2;
        Assert.Equal(expectedY, reader.View.DocumentScroller.Offset.Y, 3);
        Assert.Contains(reader.View.PageItems.GetRealizedContainers(), item =>
            item.DataContext is PdfPageViewModel { PageIndex: 25 });
        Assert.True(reader.View.PageItems.GetRealizedContainers().Count() < 10);
        Assert.Empty(reader.Launched);
    }

    [AvaloniaFact]
    public async Task RightClickMiddleClickEscapeAndCaptureLossNeverActivateALink()
    {
        using var reader = await Reader.CreateAsync();
        Point point = reader.At(25, 744);
        foreach (MouseButton button in new[] { MouseButton.Right, MouseButton.Middle })
        {
            reader.Window.MouseDown(point, button);
            reader.Window.MouseUp(point, button);
        }
        reader.Window.MouseDown(point, MouseButton.Left);
        reader.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        reader.Window.MouseUp(point, MouseButton.Left);
        Assert.Empty(reader.Launched);
        IPointer? pointer = null;
        reader.View.AddHandler(InputElement.PointerPressedEvent, (_, e) => pointer = e.Pointer,
            Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        reader.Window.MouseDown(point, MouseButton.Left);
        Assert.NotNull(pointer);
        pointer.Capture(null);
        reader.Window.MouseUp(point, MouseButton.Left);
        Assert.Empty(reader.Launched);
    }

    [AvaloniaFact]
    public async Task HighlightModeAndExistingHighlightEditingTakePriorityOverLinks()
    {
        using var reader = await Reader.CreateAsync();
        reader.Document.IsHighlightMode = true;
        Point point = reader.At(25, 744);
        reader.Window.MouseMove(point);
        Assert.False(reader.View.IsLinkCursor);
        reader.Window.MouseDown(point, MouseButton.Left);
        reader.Window.MouseUp(point, MouseButton.Left);
        await WaitUntilAsync(() => reader.Document.Highlights.Count == 1);
        Assert.Empty(reader.Launched);
        reader.Document.IsHighlightMode = false;
        reader.Window.MouseMove(point);
        Assert.False(reader.View.IsLinkCursor);
        reader.Window.MouseDown(point, MouseButton.Left);
        reader.Window.MouseUp(point, MouseButton.Left);
        Assert.Same(reader.Document.Highlights[0], reader.Document.SelectedHighlight);
        Assert.Empty(reader.Launched);
        reader.Window.MouseDown(point, MouseButton.Right);
        reader.Window.MouseUp(point, MouseButton.Right);
        Assert.Same(reader.Document.Highlights[0], reader.Document.SelectedHighlight);
        Assert.Empty(reader.Launched);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LauncherFailureIsReportedWithoutBreakingTheReader(bool throws)
    {
        using var reader = await Reader.CreateAsync();
        reader.View.LinkLauncher = _ => throws ? throw new InvalidOperationException("failed") : Task.FromResult(false);
        Point point = reader.At(25, 744);
        reader.Window.MouseDown(point, MouseButton.Left);
        reader.Window.MouseUp(point, MouseButton.Left);
        Assert.Equal("无法打开链接", Named<TextBlock>(reader.View, "LinkStatusText").Text);
        Assert.Equal(1, reader.Document.CurrentPage);
    }

    [AvaloniaTheory]
    [InlineData(PdfDestinationView.Fit)]
    [InlineData(PdfDestinationView.FitH)]
    [InlineData(PdfDestinationView.FitV)]
    [InlineData(PdfDestinationView.FitR)]
    public async Task FitDestinationsUseTheViewportAndKeepTheirRequestedPosition(PdfDestinationView mode)
    {
        using var reader = await Reader.CreateAsync();
        var destination = new PdfDestination(20, new PdfRect(0, 1000, 600, 0), View: mode,
            X: mode == PdfDestinationView.FitV ? 50 : null,
            Y: mode == PdfDestinationView.FitH ? 700 : null,
            FitRect: mode == PdfDestinationView.FitR ? new PdfRect(100, 800, 500, 500) : null);
        PdfVirtualizingPanel panel = reader.View.PageItems.GetVisualDescendants().OfType<PdfVirtualizingPanel>().Single();
        double width = reader.View.DocumentScroller.Viewport.Width - panel.Padding.Left - panel.Padding.Right;
        double height = reader.View.DocumentScroller.Viewport.Height - panel.Padding.Top - panel.Padding.Bottom;
        double expectedZoom = mode switch
        {
            PdfDestinationView.Fit => Math.Min(width / 750, height / 1250),
            PdfDestinationView.FitH => width / 750,
            PdfDestinationView.FitV => height / 1250,
            _ => Math.Min(width / 500, height / 375),
        };
        expectedZoom = Math.Clamp(expectedZoom, DocumentTabViewModel.MinZoom, DocumentTabViewModel.MaxZoom);
        reader.View.NavigateToLinkDestination(destination);
        reader.Window.UpdateLayout();
        Assert.Equal(expectedZoom, reader.Document.Zoom, 4);
        double inPageY = mode switch { PdfDestinationView.FitH => 300, PdfDestinationView.FitR => 200, _ => 0 };
        Assert.Equal(panel.Padding.Top + panel.ItemTopAt(20, expectedZoom) + inPageY * expectedZoom * 1.25,
            reader.View.DocumentScroller.Offset.Y, 3);
    }

    [AvaloniaFact]
    public async Task NullXyzRetainsZoomAndTheCurrentPageRelativeVerticalPosition()
    {
        using var reader = await Reader.CreateAsync();
        reader.View.DocumentScroller.Offset = new Vector(0, 180);
        reader.Window.UpdateLayout();
        PdfVirtualizingPanel panel = reader.View.PageItems.GetVisualDescendants().OfType<PdfVirtualizingPanel>().Single();
        reader.View.NavigateToLinkDestination(new PdfDestination(20, new PdfRect(0, 1000, 600, 0)));
        reader.Window.UpdateLayout();
        Assert.Equal(1, reader.Document.Zoom);
        Assert.Equal(panel.ItemTopAt(20, 1) + 180, reader.View.DocumentScroller.Offset.Y, 3);
    }

    [AvaloniaFact]
    public async Task ChangingDocumentsBeforeReleaseCancelsThePendingLink()
    {
        using var reader = await Reader.CreateAsync();
        using var nextService = new FakePdfDocumentService(3);
        using var nextDocument = new DocumentTabViewModel("next.pdf", nextService);
        await nextDocument.LoadAsync();
        Point point = reader.At(25, 744);
        reader.Window.MouseDown(point, MouseButton.Left);
        reader.View.DataContext = nextDocument;
        reader.Window.UpdateLayout();
        reader.Window.MouseUp(point, MouseButton.Left);
        Assert.Empty(reader.Launched);
        Assert.Equal(1, nextDocument.CurrentPage);
    }

    [AvaloniaFact]
    public async Task ZoomAnimationDoesNotActivateLinksAtTheirFutureLayoutCoordinates()
    {
        using var reader = await Reader.CreateAsync();
        reader.View.EnsureZoomController();
        reader.View.ZoomController!.ZoomTo(1.5, new Point(0, 0));
        reader.Window.UpdateLayout();
        Assert.True(reader.View.ZoomController.IsAnimating);
        Point future = reader.At(25, 744);
        reader.Window.MouseMove(future);
        Assert.False(reader.View.IsLinkCursor);
        reader.Window.MouseDown(future, MouseButton.Left);
        reader.Window.MouseUp(future, MouseButton.Left);
        Assert.Empty(reader.Launched);
        reader.View.ZoomController.FinishAnimation();
        reader.Window.UpdateLayout();
        Point settled = reader.At(25, 744);
        reader.Window.MouseMove(settled);
        Assert.True(reader.View.IsLinkCursor);
    }

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public async Task RealPdfAnnotationClickHonorsTargetCropBoxAndZoom(double dpi)
    {
        using var fixture = new LinkPdfFixture();
        using var service = new PDFiumService();
        using var document = new DocumentTabViewModel(fixture.Path, service);
        await document.LoadAsync();
        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1200, Height = 900, Content = view };
        try
        {
            window.Show();
            object impl = window.PlatformImpl!;
            impl.GetType().GetProperty("RenderScaling")!.SetValue(impl, dpi);
            ((Action<double>?)impl.GetType().GetProperty("ScalingChanged")!.GetValue(impl))?.Invoke(dpi);
            document.RenderScaling = dpi;
            window.UpdateLayout();
            await document.GetTextPageAsync(0);
            await document.GetLinkPageAsync(0);
            window.UpdateLayout();
            PdfPageImage image = view.PageItems.GetRealizedContainers()
                .SelectMany(container => container.GetVisualDescendants().OfType<PdfPageImage>()).First();
            Rect rect = image.Geometry.FromLogical(new Rect(410 * DocumentTabViewModel.LayoutScale,
                40 * DocumentTabViewModel.LayoutScale, 1, 1));
            Point point = image.TranslatePoint(rect.TopLeft, window)!.Value;
            window.MouseMove(point);
            Assert.True(view.IsLinkCursor);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            window.UpdateLayout();
            Assert.Equal(1.5, document.Zoom);
            PdfVirtualizingPanel panel = view.PageItems.GetVisualDescendants().OfType<PdfVirtualizingPanel>().Single();
            double expected = panel.Padding.Top + panel.ItemTopAt(1, 1.5) + (950 - 600) * 1.5 * DocumentTabViewModel.LayoutScale;
            Assert.Equal(expected, view.DocumentScroller.Offset.Y, 3);
            Assert.Equal(2, document.CurrentPage);
        }
        finally { window.Close(); }
    }

    private sealed class Reader : IDisposable
    {
        private readonly FakePdfDocumentService _service = new(40);
        public DocumentTabViewModel Document { get; }
        public PdfDocumentView View { get; }
        public Window Window { get; }
        public List<Uri> Launched { get; } = [];

        private Reader()
        {
            Document = new DocumentTabViewModel("unused.pdf", _service);
            View = new PdfDocumentView { DataContext = Document };
            View.LinkLauncher = uri => { Launched.Add(uri); return Task.FromResult(true); };
            Window = new Window { Width = 1000, Height = 750, Content = View };
        }

        public static async Task<Reader> CreateAsync()
        {
            var reader = new Reader();
            await reader.Document.LoadAsync();
            reader.Window.Show();
            reader.Window.UpdateLayout();
            PdfTextPage text = await reader.Document.GetTextPageAsync(0);
            await reader.Document.GetLinkPageAsync(0);
            reader.Document.Pages[0].SetLinks(new PdfLinkPage(0, text.CropBox,
            [
                new PdfLink([PdfLinkRegion.FromRect(new PdfRect(20, 751, 100, 737))], PdfLinkTarget.FromUri("https://example.com/target")),
                new PdfLink([PdfLinkRegion.FromRect(new PdfRect(200, 750, 300, 700))],
                    new PdfLinkTarget(Destination: new PdfDestination(25, new PdfRect(0, 900, 600, 0), X: 200, Y: 700, Zoom: 2))),
            ]));
            reader.Window.UpdateLayout();
            return reader;
        }

        public Point At(double x, double y)
        {
            PdfPageImage image = View.PageItems.GetRealizedContainers()
                .SelectMany(container => container.GetVisualDescendants().OfType<PdfPageImage>())
                .First(image => image.DataContext is PdfPageViewModel { PageIndex: 0 });
            double scale = DocumentTabViewModel.LayoutScale * Document.Zoom;
            PdfRect box = Document.Pages[0].Text!.CropBox;
            Rect rect = image.Geometry.FromLogical(new Rect((x - box.L) * scale, (box.T - y) * scale, 1, 1));
            return image.TranslatePoint(rect.TopLeft, Window)!.Value;
        }

        public void Dispose() { Window.Close(); Document.Dispose(); _service.Dispose(); }
    }
}
