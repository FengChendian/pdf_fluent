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
    private static readonly TimeSpan ReaderFlyoutAnimationDuration = TimeSpan.FromMilliseconds(180);

    private PdfZoomController? _zoomController;
    private TopLevel? _topLevel;
    private IDisposable? _renderScalingTimer;
    private IDisposable? _pageIndicatorTimer;
    private IDisposable? _tocCloseTimer;
    private DocumentTabViewModel? _observedDocument;
    private Vector? _scrollOffsetToRestore;
    private bool _deferredPageRenders;
    private int _lastObservedPageNumber = 1;
    private int? _pendingPageIndex;

    public PdfDocumentView()
    {
        InitializeComponent();
        InitializeHighlightPalette();
        InitializeTextInteraction();
        DocumentScroller.ScrollChanged += OnDocumentScrollChanged;
        TableOfContentsButton.IsCheckedChanged += OnTableOfContentsButtonToggled;
        TableOfContentsOverlay.Transitions = CreateReaderFlyoutTransitions();
        PageJumpOverlay.Transitions = CreateReaderFlyoutTransitions();
        AddHandler(PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnPageJumpPointerPressed, RoutingStrategies.Tunnel);
    }

    private static Avalonia.Animation.Transitions CreateReaderFlyoutTransitions() =>
        [
            new Avalonia.Animation.DoubleTransition
            {
                Property = OpacityProperty,
                Duration = ReaderFlyoutAnimationDuration,
            },
            new Avalonia.Animation.ThicknessTransition
            {
                Property = MarginProperty,
                Duration = ReaderFlyoutAnimationDuration,
            },
        ];

    internal PdfZoomController? ZoomController => _zoomController;
    internal TimeSpan PageIndicatorAutoHideDelay { get; set; } = TimeSpan.FromMilliseconds(800);
    internal bool IsPageIndicatorVisible => PageIndicatorOverlay.IsVisible;
    internal bool IsTableOfContentsVisible => TableOfContentsOverlay.IsVisible;
    internal bool IsPageJumpVisible => PageJumpOverlay.IsVisible;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var document = DataContext as DocumentTabViewModel;
        _observedDocument ??= document;
        if (document is not null) _scrollOffsetToRestore ??= document.ScrollOffset;
        AttachTextDocument(document);
        AttachRenderScaling();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        CaptureScrollOffset();
        var document = DataContext as DocumentTabViewModel;
        _observedDocument = document;
        _scrollOffsetToRestore = document?.ScrollOffset;
        HidePageIndicator();
        CloseTableOfContentsImmediately();
        ClosePageJump();
        CloseHighlightPalette();
        AttachTextDocument(document);
        _lastObservedPageNumber = document?.CurrentPage ?? 1;
        _pendingPageIndex = null;
        ResetZoomController();
        base.OnDataContextChanged(e);
        ApplyRenderScaling();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CaptureScrollOffset();
        _observedDocument = null;
        _scrollOffsetToRestore = null;
        DetachRenderScaling();
        DetachTextInteraction();
        HidePageIndicator();
        CloseTableOfContentsImmediately();
        ClosePageJump();
        CloseHighlightPalette();
        ResetZoomController();
        base.OnDetachedFromVisualTree(e);
    }

    private void CaptureScrollOffset()
    {
        if (_scrollOffsetToRestore is null) _observedDocument?.UpdateScrollOffset(DocumentScroller.Offset);
    }

    private void OnDocumentScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.OffsetDelta != default)
        {
            _linkHover = null;
            if (_pressedLink is not null) _linkClickCanceled = true;
            SetLinkStatus(null);
            if (!_isSelectingText) DocumentScroller.Cursor = DefaultCursor;
        }
        if (_scrollOffsetToRestore is null) _observedDocument?.UpdateScrollOffset(DocumentScroller.Offset);
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
        ClosePageJump();
        int currentIndex = _pendingPageIndex ?? document.CurrentPage - 1;
        int targetIndex = Math.Clamp(currentIndex + delta, 0, document.PageCount - 1);
        ScrollToPage(targetIndex);
    }

    private void ScrollToPage(int targetIndex)
    {
        if (DataContext is not DocumentTabViewModel document) return;
        _pendingPageIndex = targetIndex;
        PageItems.ScrollIntoView(targetIndex);
        document.CurrentPage = targetIndex + 1;
    }

    private void OnTocItemClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DocumentTabViewModel document ||
            (sender as Control)?.DataContext is not TocEntryViewModel entry ||
            entry.PageIndex < 0 || entry.PageIndex >= document.PageCount)
        {
            return;
        }

        ScrollToPage(entry.PageIndex);
    }

    private void OnPageJumpButtonToggled(object? sender, RoutedEventArgs e)
    {
        if (PageJumpButton.IsChecked != true)
        {
            PageJumpOverlay.Classes.Remove("open");
            PageJumpOverlay.IsVisible = false;
            return;
        }

        if (DataContext is not DocumentTabViewModel { PageCount: > 0 } document)
        {
            ClosePageJump();
            return;
        }

        CloseTableOfContentsImmediately();
        CloseHighlightPalette();
        PageJumpTextBox.Text = document.CurrentPage.ToString();
        PageJumpError.IsVisible = false;
        PageJumpOverlay.IsVisible = true;
        PageJumpOverlay.Classes.Add("open");
        PageJumpTextBox.Focus();
        PageJumpTextBox.SelectAll();
    }

    private void OnPageJumpTextChanged(object? sender, TextChangedEventArgs e) =>
        PageJumpError.IsVisible = false;

    private void OnPageJumpTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            NavigateToEnteredPage();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ClosePageJump();
            PageJumpButton.Focus();
            e.Handled = true;
        }
    }

    private void OnGoToPageClick(object? sender, RoutedEventArgs e) => NavigateToEnteredPage();

    private void NavigateToEnteredPage()
    {
        if (DataContext is not DocumentTabViewModel document || document.PageCount == 0) return;
        if (!int.TryParse(PageJumpTextBox.Text, out int pageNumber) ||
            pageNumber < 1 || pageNumber > document.PageCount)
        {
            PageJumpError.Text = $"请输入 1 到 {document.PageCount} 之间的页码";
            PageJumpError.IsVisible = true;
            PageJumpTextBox.Focus();
            PageJumpTextBox.SelectAll();
            return;
        }

        ClosePageJump();
        ScrollToPage(pageNumber - 1);
        DocumentScroller.Focus();
    }

    private void OnPageJumpPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!PageJumpOverlay.IsVisible || e.Source is not Visual source) return;
        if (IsWithin(source, PageJumpOverlay) || IsWithin(source, PageNavigation)) return;
        ClosePageJump();
    }

    private static bool IsWithin(Visual source, Visual ancestor) =>
        ReferenceEquals(source, ancestor) || source.GetVisualAncestors().Contains(ancestor);

    private void ClosePageJump()
    {
        PageJumpButton.IsChecked = false;
        PageJumpOverlay.Classes.Remove("open");
        PageJumpOverlay.IsVisible = false;
        PageJumpError.IsVisible = false;
    }

    private void OnTableOfContentsButtonToggled(object? sender, RoutedEventArgs e)
    {
        _tocCloseTimer?.Dispose();
        _tocCloseTimer = null;

        if (TableOfContentsButton.IsChecked == true)
        {
            ClosePageJump();
            CloseHighlightPalette();
            TableOfContentsOverlay.IsVisible = true;
            TableOfContentsOverlay.Classes.Add("open");
            return;
        }

        TableOfContentsOverlay.Classes.Remove("open");
        _tocCloseTimer = DispatcherTimer.RunOnce(
            HideTableOfContentsAfterClose,
            ReaderFlyoutAnimationDuration);
    }

    private void HideTableOfContentsAfterClose()
    {
        _tocCloseTimer = null;
        if (TableOfContentsButton.IsChecked != true) TableOfContentsOverlay.IsVisible = false;
    }

    private void CloseTableOfContentsImmediately()
    {
        if (DataContext is DocumentTabViewModel document)
            document.IsTableOfContentsOpen = false;

        _tocCloseTimer?.Dispose();
        _tocCloseTimer = null;
        TableOfContentsOverlay.Classes.Remove("open");
        TableOfContentsOverlay.IsVisible = false;
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
        // A reused reader initially realizes containers at the previous tab's
        // offset. Wait for restoration before starting work for those pages.
        if (_scrollOffsetToRestore is not null)
        {
            _deferredPageRenders = true;
            return;
        }
        if (DataContext is DocumentTabViewModel document &&
            e.Container.DataContext is PdfPageViewModel page)
        {
            var panel = PageItems.GetVisualDescendants().OfType<PdfVirtualizingPanel>().FirstOrDefault();
            document.EnsurePageRendered(page, panel?.IsContainerVisible(e.Container) ?? true);
        }
    }

    private bool RestoreScrollOffset(DocumentTabViewModel document)
    {
        if (_scrollOffsetToRestore is not Vector offset ||
            document.PageCount == 0 || DocumentScroller.Extent.Height <= 0)
        {
            return false;
        }

        _scrollOffsetToRestore = null;
        double maxX = Math.Max(0, DocumentScroller.Extent.Width - DocumentScroller.Viewport.Width);
        double maxY = Math.Max(0, DocumentScroller.Extent.Height - DocumentScroller.Viewport.Height);
        Vector restored = new(
            Math.Clamp(offset.X, 0, maxX),
            Math.Clamp(offset.Y, 0, maxY));
        bool changed = DocumentScroller.Offset != restored;
        DocumentScroller.Offset = restored;
        return changed;
    }

    private void OnPagesLayoutUpdated(object? sender, EventArgs e)
    {
        EnsureZoomController();

        if (DataContext is not DocumentTabViewModel document ||
            sender is not ItemsControl itemsControl)
        {
            return;
        }

        // Changing the offset schedules another layout with the correct
        // realization range. The current containers still belong to the old
        // viewport and must not enqueue renders or overwrite CurrentPage.
        if (RestoreScrollOffset(document) || _scrollOffsetToRestore is not null) return;
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
        if (_deferredPageRenders)
        {
            _deferredPageRenders = false;
            foreach (PdfPageViewModel page in realizedPages)
                document.EnsurePageRendered(page, visiblePages.Contains(page));
        }
        // Layout has settled the actual page, even when the requested page
        // cannot reach the viewport top because scrolling is clamped at the end.
        // Keep pending targets only between clicks before this layout pass.
        _pendingPageIndex = null;
        if (_lastObservedPageNumber != document.CurrentPage)
        {
            _lastObservedPageNumber = document.CurrentPage;
            ShowPageIndicator();
        }
    }
}
