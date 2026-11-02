using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Rendering.Composition;
using Avalonia.VisualTree;
using pdf_fluent.Controls;
using pdf_fluent.ViewModels;
using pdf_fluent.Views;
using Xunit;

namespace pdf_fluent.Tests;

/// <summary>
/// A document view hosted in a headless window, with helpers to locate page
/// surfaces in viewport coordinates. Shared by the zoom and render tests.
///
/// <para>
/// The zoom animation is applied by the compositor (<c>Scale</c> about
/// <c>CenterPoint</c>), so layout coordinates alone only describe the
/// committed state. <see cref="VisualPageRect"/> applies the same transform
/// the render thread applies, using the controller's logical zoom clock and
/// the composition origin; <see cref="PanelVisual"/> exposes the composition
/// state that is actually handed to the render thread.
/// </para>
/// </summary>
internal sealed class ZoomTestHarness : IDisposable
{
    private ZoomTestHarness(
        FakePdfDocumentService service,
        DocumentTabViewModel document,
        PdfDocumentView view,
        Window window)
    {
        Service = service;
        Document = document;
        View = view;
        Window = window;
    }

    public FakePdfDocumentService Service { get; }
    public DocumentTabViewModel Document { get; }
    public PdfDocumentView View { get; }
    public Window Window { get; }
    public Size Viewport => Zoom.InputSource.Bounds.Size;

    public ScrollViewer Scroller =>
        Window.GetVisualDescendants().OfType<ScrollViewer>().First();

    public ItemsControl Items =>
        Window.GetVisualDescendants().OfType<ItemsControl>().First();

    public PdfVirtualizingPanel Panel =>
        Window.GetVisualDescendants().OfType<PdfVirtualizingPanel>().First();

    /// <summary>The composition visual the zoom scale animation runs on.</summary>
    public CompositionVisual PanelVisual =>
        ElementComposition.GetElementVisual(Panel)
        ?? throw new InvalidOperationException("虚拟化面板没有组合视觉。");

    /// <summary>Zoom of the visual state relative to the committed layout.</summary>
    public double VisualScale =>
        Document.Zoom > 0 ? Document.VisualZoom / Document.Zoom : 1;

    public PdfZoomController Zoom
    {
        get
        {
            View.EnsureZoomController();
            return View.ZoomController
                   ?? throw new InvalidOperationException("缩放控制器未创建。");
        }
    }

    public static async Task<ZoomTestHarness> CreateAsync(
        int pageCount = 8,
        double windowWidth = 1000,
        double windowHeight = 700)
    {
        var service = new FakePdfDocumentService(pageCount);
        var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();

        var view = new PdfDocumentView { DataContext = document };
        var window = new Window
        {
            Width = windowWidth,
            Height = windowHeight,
            Content = view,
        };
        window.Show();
        window.UpdateLayout();

        return new ZoomTestHarness(service, document, view, window);
    }

    public void UpdateLayout() => Window.UpdateLayout();

    public void SetZoomImmediately(double zoom) => Zoom.ZoomTo(zoom, null, animate: false);

    /// <summary>Number of render calls the fake service made for a page.</summary>
    public int RenderCountFor(int pageIndex) => Service.RenderedPages.Count(index => index == pageIndex);

    public Point ToWindow(Point viewportPoint) =>
        Zoom.InputSource.TranslatePoint(viewportPoint, Window)
        ?? throw new InvalidOperationException("无法转换坐标。");

    public Rect PageRect(int pageIndex) => PageSurfaceRect(PageSurface(pageIndex));

    /// <summary>
    /// The page surface as the compositor currently shows it: the committed
    /// layout rect scaled about the composition origin by
    /// <see cref="VisualScale"/>, exactly like <c>CompositionVisual.Scale</c> and
    /// <c>CompositionVisual.CenterPoint</c> are applied by the render thread.
    /// </summary>
    public Rect VisualPageRect(int pageIndex) =>
        ScaleAbout(PageRect(pageIndex), VisualScale, Zoom.VisualOriginViewport);

    /// <summary>The viewport position of a page fraction in the animated visual state.</summary>
    public Point VisualPoint(int pageIndex, double fractionX, double fractionY)
    {
        Rect rect = VisualPageRect(pageIndex);
        return new Point(rect.X + (fractionX * rect.Width), rect.Y + (fractionY * rect.Height));
    }

    public static Rect ScaleAbout(Rect rect, double scale, Point origin) => new(
        origin.X + ((rect.X - origin.X) * scale),
        origin.Y + ((rect.Y - origin.Y) * scale),
        rect.Width * scale,
        rect.Height * scale);

    /// <summary>The white page surface Border of a realized page container.</summary>
    public Border PageSurface(int pageIndex)
    {
        Control container = Items.GetRealizedContainers()
            .First(c => c.DataContext is PdfPageViewModel page && page.PageIndex == pageIndex);
        return container.GetVisualDescendants()
            .OfType<Border>()
            .First(border => border.Child is Grid);
    }

    public bool PlaceholderVisible(int pageIndex)
    {
        Control container = Items.GetRealizedContainers()
            .First(c => c.DataContext is PdfPageViewModel page && page.PageIndex == pageIndex);
        return container.GetVisualDescendants().OfType<ProgressBar>().First().IsVisible;
    }

    private Rect PageSurfaceRect(Control container)
    {
        // The item template's page surface is the white Border; its rect is the
        // part of the item that scales with the zoom (the footer below it does
        // not).
        var surface = container.GetVisualDescendants()
            .OfType<Border>()
            .First(border => border.Child is Grid);
        return PageSurfaceRect(surface);
    }

    private Rect PageSurfaceRect(Border surface)
    {
        Point origin = surface.TranslatePoint(default, Zoom.InputSource)
                       ?? throw new InvalidOperationException("无法转换页面坐标。");
        Point opposite = surface.TranslatePoint(
                             new Point(surface.Bounds.Width, surface.Bounds.Height),
                             Zoom.InputSource)
                         ?? throw new InvalidOperationException("无法转换页面坐标。");
        return new Rect(origin, opposite);
    }

    public (PdfPageViewModel Page, Rect Rect) PageAt(int pageIndex)
    {
        PdfPageViewModel page = Document.Pages[pageIndex];
        return (page, PageRect(pageIndex));
    }

    public (PdfPageViewModel Page, Rect Rect) PageAt(Point viewportPoint)
    {
        foreach (Control container in Items.GetRealizedContainers())
        {
            if (container.DataContext is not PdfPageViewModel page) continue;

            Rect rect = PageSurfaceRect(container);
            if (rect.Contains(viewportPoint)) return (page, rect);
        }

        throw new InvalidOperationException($"({viewportPoint.X}, {viewportPoint.Y}) 处没有已实现的页面。");
    }

    public async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("等待页面渲染超时。");
            await Task.Delay(10);
        }
    }

    public void Dispose()
    {
        Window.Close();
        Service.Dispose();
        Document.Dispose();
    }
}
