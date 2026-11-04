using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using pdf_fluent.Controls;
using pdf_fluent.ViewModels;

namespace pdf_fluent.Views;

public partial class PdfDocumentView : UserControl
{
    /// <summary>Zoom step of the toolbar buttons and of one Ctrl+wheel notch.</summary>
    private const double ZoomStep = 0.25;

    /// <summary>
    /// Moving a window between monitors raises several scaling changes; the
    /// re-render of the visible pages is coalesced over this delay.
    /// </summary>
    private static readonly TimeSpan RenderScalingDebounce = TimeSpan.FromMilliseconds(180);

    private PdfZoomController? _zoomController;
    private TopLevel? _topLevel;
    private IDisposable? _renderScalingTimer;
    private IDisposable? _pageIndicatorTimer;
    private int _lastObservedPageNumber = 1;
    private int? _pendingPageIndex;

    public PdfDocumentView()
    {
        InitializeComponent();
        AddHandler(PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Tunnel);
    }

    internal PdfZoomController? ZoomController => _zoomController;
    internal TimeSpan PageIndicatorAutoHideDelay { get; set; } = TimeSpan.FromMilliseconds(800);
    internal bool IsPageIndicatorVisible => PageIndicatorOverlay.IsVisible;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AttachRenderScaling();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        HidePageIndicator();
        _lastObservedPageNumber = (DataContext as DocumentTabViewModel)?.CurrentPage ?? 1;
        _pendingPageIndex = null;
        ResetZoomController();
        base.OnDataContextChanged(e);
        ApplyRenderScaling();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DetachRenderScaling();
        HidePageIndicator();
        ResetZoomController();
        base.OnDetachedFromVisualTree(e);
    }

    private void ResetZoomController()
    {
        _zoomController?.Detach();
        _zoomController = null;
    }

    // ── HiDPI: render bitmaps at physical pixels ─────────────────────
    //
    // The page bitmaps are rendered at LayoutScale * zoom * RenderScaling
    // device pixels per PDF point.  RenderScaling belongs to the monitor the
    // window is currently on, so a 4K screen (200%) gets twice the bitmap
    // resolution a 1080p screen (100%) gets.  Moving the window to a monitor
    // with a different scale raises ScalingChanged and re-renders the pages
    // that are on screen; every other bitmap is re-rendered lazily the next
    // time it becomes visible (the per-page scale check).

    private void AttachRenderScaling()
    {
        DetachRenderScaling();

        if (TopLevel.GetTopLevel(this) is not { } topLevel) return;
        _topLevel = topLevel;
        topLevel.ScalingChanged += OnTopLevelScalingChanged;
        ApplyRenderScaling();
    }

    private void DetachRenderScaling()
    {
        _renderScalingTimer?.Dispose();
        _renderScalingTimer = null;

        if (_topLevel is { } topLevel) topLevel.ScalingChanged -= OnTopLevelScalingChanged;
        _topLevel = null;
    }

    private void OnTopLevelScalingChanged(object? sender, EventArgs e)
    {
        _renderScalingTimer?.Dispose();
        _renderScalingTimer = DispatcherTimer.RunOnce(ApplyRenderScaling, RenderScalingDebounce);
    }

    private void ApplyRenderScaling()
    {
        _renderScalingTimer = null;
        if (_topLevel is not { } topLevel) return;
        if (DataContext is not DocumentTabViewModel document) return;

        document.RenderScaling = topLevel.RenderScaling;
    }

    /// <summary>
    /// The virtualizing panel only exists once the items have been generated,
    /// so the zoom controller is created after the first layout pass.
    /// </summary>
    internal void EnsureZoomController()
    {
        if (_zoomController is not null) return;
        if (DataContext is not DocumentTabViewModel document) return;

        PdfVirtualizingPanel? panel = PageItems
            .GetVisualDescendants()
            .OfType<PdfVirtualizingPanel>()
            .FirstOrDefault();
        if (panel is null) return;

        _zoomController = new PdfZoomController(DocumentScroller, panel, document);
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y != 0 && !e.KeyModifiers.HasFlag(KeyModifiers.Control)) _pendingPageIndex = null;
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Delta.Y == 0) return;

        EnsureZoomController();
        if (_zoomController is null) return;

        e.Handled = true;
        _zoomController.ZoomBy(
            Math.Sign(e.Delta.Y) * ZoomStep,
            ViewportAnchorWithin(_zoomController, e));
    }

    /// <summary>
    /// Ctrl+wheel is handled for the whole view, including the toolbar.  Only a
    /// pointer that is actually over the document is used as zoom anchor; the
    /// toolbar zooms around the centre of the document viewport.
    /// </summary>
    private static Point? ViewportAnchorWithin(PdfZoomController controller, PointerWheelEventArgs e)
    {
        Point position = e.GetPosition(controller.InputSource);
        Size viewport = controller.InputSource.Bounds.Size;
        bool inside = position.X >= 0 && position.Y >= 0 &&
                      position.X <= viewport.Width && position.Y <= viewport.Height;
        return inside ? position : null;
    }

    private void OnZoomInClick(object? sender, RoutedEventArgs e) => ZoomFromViewportCenter(ZoomStep);

    private void OnZoomOutClick(object? sender, RoutedEventArgs e) => ZoomFromViewportCenter(-ZoomStep);

    private void ZoomFromViewportCenter(double delta)
    {
        EnsureZoomController();
        _zoomController?.ZoomBy(delta, anchorViewportPoint: null);
    }

    private void OnPreviousPageClick(object? sender, RoutedEventArgs e) => ScrollToRelativePage(-1);

    private void OnNextPageClick(object? sender, RoutedEventArgs e) => ScrollToRelativePage(1);

    private void ScrollToRelativePage(int delta)
    {
        if (DataContext is not DocumentTabViewModel document || document.PageCount == 0) return;
        int currentIndex = _pendingPageIndex ?? document.CurrentPage - 1;
        int targetIndex = Math.Clamp(currentIndex + delta, 0, document.PageCount - 1);
        _pendingPageIndex = targetIndex;
        PageItems.ScrollIntoView(targetIndex);
        document.CurrentPage = targetIndex + 1;
    }

    private void OnFavoriteClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DocumentTabViewModel document &&
            TopLevel.GetTopLevel(this)?.DataContext is MainViewModel mainViewModel)
        {
            mainViewModel.ToggleFavoriteCommand.Execute(document);
        }
    }

    internal void ShowPageIndicator()
    {
        if (DataContext is not DocumentTabViewModel { PageCount: > 0 }) return;

        _pageIndicatorTimer?.Dispose();
        PageIndicatorOverlay.IsVisible = true;
        _pageIndicatorTimer = DispatcherTimer.RunOnce(HidePageIndicator, PageIndicatorAutoHideDelay);
    }

    internal void HidePageIndicator()
    {
        _pageIndicatorTimer?.Dispose();
        _pageIndicatorTimer = null;
        PageIndicatorOverlay.IsVisible = false;
    }

    private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (DataContext is DocumentTabViewModel document &&
            e.Container.DataContext is PdfPageViewModel page)
        {
            var panel = PageItems.GetVisualDescendants().OfType<PdfVirtualizingPanel>().FirstOrDefault();
            document.EnsurePageRendered(page, panel?.IsContainerVisible(e.Container) ?? true);
        }
    }

    private void OnPagesLayoutUpdated(object? sender, EventArgs e)
    {
        EnsureZoomController();

        if (DataContext is not DocumentTabViewModel document ||
            sender is not ItemsControl itemsControl)
        {
            return;
        }

        var panel = itemsControl.GetVisualDescendants().OfType<PdfVirtualizingPanel>().FirstOrDefault();
        List<PdfPageViewModel> realizedPages = [];
        List<PdfPageViewModel> visiblePages = [];
        foreach (Control container in itemsControl.GetRealizedContainers())
        {
            if (container.DataContext is not PdfPageViewModel page) continue;

            realizedPages.Add(page);
            if (panel?.IsContainerVisible(container) ?? true) visiblePages.Add(page);
        }

        document.SyncRealizedPages(
            realizedPages, visiblePages, panel?.ScrollDirection ?? 0, panel?.CurrentPageIndex);
        if (_pendingPageIndex is int pendingPageIndex &&
            document.CurrentPage == pendingPageIndex + 1)
        {
            _pendingPageIndex = null;
        }
        if (_lastObservedPageNumber != document.CurrentPage)
        {
            _lastObservedPageNumber = document.CurrentPage;
            ShowPageIndicator();
        }
    }
}
