using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using pdf_fluent.ViewModels;

namespace pdf_fluent.Controls;

/// <summary>
/// Pointer-anchored, animated zoom for the virtualized page list.
///
/// <para>
/// The target layout and scroll offset are committed whenever the target
/// changes. Starting containers are retained while the target range is
/// realized, then a render transform animates the committed layout without
/// further page measure, scroll, or realization work on each frame.
/// </para>
/// <para>
/// An axis is only anchored while the document is larger than the viewport on
/// that axis, i.e. while the scrollbar can actually represent the computed
/// offset.  While an axis fits (a page narrower than the window is the common
/// case horizontally) the document stays centred on that axis: anchoring it
/// there would push the page off-centre although no scrollbar exists, and the
/// layout would land somewhere the user cannot pan back from.
/// </para>
/// <para>
/// Bitmaps are left untouched while the animation runs; the page keeps showing
/// its previous render (stretched to the new size) and is re-rendered once the
/// zoom settles, so the zoom never goes through a blank frame.
/// </para>
/// </summary>
internal sealed class PdfZoomController
{
    /// <summary>Fluent's point-to-point animation duration.</summary>
    private const double AnimationDurationMs = 210;

    // Fluent "decelerate" interpolation (WinUI's cubic-bezier(0.1, 0.9, 0.2, 1.0)).
    private const double EaseX1 = 0.1;
    private const double EaseY1 = 0.9;
    private const double EaseX2 = 0.2;
    private const double EaseY2 = 1.0;

    private static readonly TimeSpan FallbackFrameDelta = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan MaxFrameDelta = TimeSpan.FromMilliseconds(100);

#if DEBUG
    /// <summary>Frames slower than this are reported to the debug output.</summary>
    private const double SlowFrameMilliseconds = 25;
#endif

    /// <summary>
    /// Width (in layout units of horizontal scroll room) over which the
    /// horizontal layout hands over from "document centred" to "page point
    /// pinned under the cursor".  Both layouts put the page at exactly the same
    /// place at the fill threshold, but their velocities differ - without the
    /// hand-over band the document visibly jolts at the moment it stops filling
    /// the window (measured: the page edge moved 34 px in the crossing frame
    /// against 12-13 px in the frames around it).  Inside the band the cursor
    /// anchor is honoured only partially (by at most a quarter of the band,
    /// i.e. a few pixels): there is no scroll room to do better anyway.
    /// </summary>
    private const double AnchorHandoverBand = 48;

    private readonly ScrollViewer _scrollViewer;
    private readonly PdfVirtualizingPanel _panel;
    private readonly DocumentTabViewModel _document;
    private readonly ScaleTransform _zoomTransform = new();

    private bool _frameScheduled;
    private bool _lastTickValid;
    private TimeSpan _lastTick;
    private double _elapsedMs;
    private double _fromZoom;
    private double _targetZoom;
    private double _displayZoom = 1;
    private Point _transformOriginViewport;
    private ZoomAnchor _anchor;

    public PdfZoomController(
        ScrollViewer scrollViewer,
        PdfVirtualizingPanel panel,
        DocumentTabViewModel document)
    {
        _scrollViewer = scrollViewer;
        _panel = panel;
        _document = document;
    }

    public bool IsAnimating { get; private set; }

    /// <summary>
    /// The visual viewport coordinates of the pointer are expressed in.  The
    /// scroll offset lives in the content presenter's coordinate space, so
    /// pointer positions have to be measured against it.
    /// </summary>
    public Control InputSource => Presenter ?? (Control)_scrollViewer;

    private ScrollContentPresenter? Presenter => _scrollViewer.Presenter as ScrollContentPresenter;

    private Size Viewport => Presenter?.Viewport ?? _scrollViewer.Viewport;

    public void ZoomBy(double zoomDelta, Point? anchorViewportPoint)
    {
        // While an animation runs the target is accumulated instead of being
        // derived from the currently displayed zoom, so that fast wheel input
        // does not lose zoom steps.
        double basis = IsAnimating ? _targetZoom : _document.Zoom;
        ZoomTo(basis + zoomDelta, anchorViewportPoint);
    }

    public void ZoomTo(double targetZoom, Point? anchorViewportPoint, bool animate = true)
    {
        if (_document.Pages.Count == 0) return;

        double clamped = Math.Clamp(targetZoom, DocumentTabViewModel.MinZoom, DocumentTabViewModel.MaxZoom);
        if (IsAnimating)
        {
            if (Math.Abs(clamped - _targetZoom) < 1e-9) return;
        }
        else if (Math.Abs(clamped - _document.Zoom) < 1e-9)
        {
            return;
        }

        Point anchorPoint = anchorViewportPoint ?? DefaultAnchorPoint();
        if (IsAnimating)
        {
            double correction = _displayZoom / _document.Zoom;
            var layoutAnchorPoint = new Point(
                _transformOriginViewport.X +
                (anchorPoint.X - _transformOriginViewport.X) / correction,
                _transformOriginViewport.Y +
                (anchorPoint.Y - _transformOriginViewport.Y) / correction);
            _anchor = CaptureAnchor(layoutAnchorPoint) with { ViewportPoint = anchorPoint };
            _fromZoom = _displayZoom;
        }
        else
        {
            _anchor = CaptureAnchor(anchorPoint);
            _fromZoom = _document.Zoom;
        }

        _targetZoom = clamped;
        _elapsedMs = 0;
        _lastTickValid = false;
        IsAnimating = true;

        _document.SuspendRendering();
        _panel.BeginZoomAnimation();
        CommitTargetLayout();

        if (!animate) Settle();
        else ScheduleFrame();
    }

    /// <summary>Advances the animation by one frame. Exposed for deterministic tests.</summary>
    internal void Advance(TimeSpan delta)
    {
        if (!IsAnimating) return;

        _elapsedMs += Math.Clamp(delta.TotalMilliseconds, 0, MaxFrameDelta.TotalMilliseconds);
        double progress = Math.Clamp(_elapsedMs / AnimationDurationMs, 0, 1);

        if (progress >= 1)
        {
            Settle();
            return;
        }

        double zoom = _fromZoom + (_targetZoom - _fromZoom) * FluentEase(progress);
        ApplyVisualState(zoom);
    }

    /// <summary>Jumps to the end of the running animation. Exposed for tests.</summary>
    internal void FinishAnimation()
    {
        if (!IsAnimating) return;
        Settle();
    }

    public void Detach()
    {
        IsAnimating = false;
        _frameScheduled = false;
        _panel.RenderTransform = null;
        _panel.EndZoomAnimation();
        _document.SetVisualZoom(null);
        _document.ResumeRendering();
    }

    // ── Frame scheduling ─────────────────────────────────────────────

    private void ScheduleFrame()
    {
        if (_frameScheduled) return;

        if (TopLevel.GetTopLevel(_scrollViewer) is not { } topLevel)
        {
            FinishAnimation();
            return;
        }

        _frameScheduled = true;
        topLevel.RequestAnimationFrame(OnAnimationFrame);
    }

    private void OnAnimationFrame(TimeSpan timestamp)
    {
        _frameScheduled = false;
        if (!IsAnimating) return;

        TimeSpan delta = _lastTickValid ? timestamp - _lastTick : FallbackFrameDelta;

#if DEBUG
        // Frame-interval diagnostics: a zoom that stutters shows up here as a
        // gap that is much larger than the surrounding frames.
        if (_lastTickValid && delta.TotalMilliseconds > SlowFrameMilliseconds)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[zoom] slow frame {delta.TotalMilliseconds:F1} ms at zoom {_displayZoom:F3}");
        }
#endif

        _lastTick = timestamp;
        _lastTickValid = true;

        Advance(delta);
        if (IsAnimating) ScheduleFrame();
    }

    private void Settle()
    {
        IsAnimating = false;
        _frameScheduled = false;
        _displayZoom = _targetZoom;
        _zoomTransform.ScaleX = 1;
        _zoomTransform.ScaleY = 1;
        _panel.RenderTransform = null;
        _panel.EndZoomAnimation();
        if (TopLevel.GetTopLevel(_scrollViewer) is not null) _scrollViewer.UpdateLayout();
        _document.SetVisualZoom(null);
        _document.ResumeRendering();
    }

    private void CommitTargetLayout()
    {
        _panel.RenderTransform = null;
        ApplyState(_targetZoom, _anchor);
        if (TopLevel.GetTopLevel(_scrollViewer) is not null) _scrollViewer.UpdateLayout();

        Point targetAnchor = AnchorViewportPoint(_targetZoom, _anchor);
        double initialScale = _fromZoom / _targetZoom;
        double denominator = 1 - initialScale;
        _transformOriginViewport = Math.Abs(denominator) < 1e-9
            ? _anchor.ViewportPoint
            : new Point(
                (_anchor.ViewportPoint.X - initialScale * targetAnchor.X) / denominator,
                (_anchor.ViewportPoint.Y - initialScale * targetAnchor.Y) / denominator);

        Vector offset = _scrollViewer.Offset;
        _panel.FreezeZoomViewport();
        _panel.RenderTransformOrigin = new RelativePoint(
            new Point(
                offset.X + _transformOriginViewport.X,
                offset.Y + _transformOriginViewport.Y),
            RelativeUnit.Absolute);
        ApplyVisualState(_fromZoom);
        _panel.RenderTransform = _zoomTransform;
    }

    private void ApplyVisualState(double zoom)
    {
        _displayZoom = zoom;
        double scale = zoom / _targetZoom;
        _zoomTransform.ScaleX = scale;
        _zoomTransform.ScaleY = scale;
        _document.SetVisualZoom(zoom);
    }

    // ── Anchoring ────────────────────────────────────────────────────

    /// <summary>
    /// A point of a page (given as a fraction of the page box) together with
    /// the viewport position it has to stay at.
    /// </summary>
    private readonly record struct ZoomAnchor(
        int PageIndex,
        double FractionX,
        double FractionY,
        Point ViewportPoint);

    private Point DefaultAnchorPoint()
    {
        Size viewport = Viewport;
        return new Point(viewport.Width / 2, viewport.Height / 2);
    }

    private ZoomAnchor CaptureAnchor(Point viewportPoint)
    {
        double zoom = _document.Zoom;
        LayoutGeometry geometry = MeasureLayout(zoom);
        Vector offset = _scrollViewer.Offset;

        double contentX = viewportPoint.X + offset.X;
        double contentY = viewportPoint.Y + offset.Y;

        int index = FindPageIndex(contentY - geometry.PaddingTop, zoom);
        PdfPageViewModel page = _document.Pages[index];

        double pageWidth = PdfPageViewModel.PageWidthAt(page, zoom);
        double pageHeight = PdfPageViewModel.PageHeightAt(page, zoom);
        double pageLeft = geometry.PaddingLeft + ItemLeftInPanel(geometry, index, zoom);
        double pageTop = geometry.PaddingTop + ItemTopInPanel(index, zoom);

        return new ZoomAnchor(
            index,
            (contentX - pageLeft) / pageWidth,
            (contentY - pageTop) / pageHeight,
            viewportPoint);
    }

    private Point AnchorViewportPoint(double zoom, in ZoomAnchor anchor)
    {
        LayoutGeometry geometry = MeasureLayout(zoom);
        Vector offset = _scrollViewer.Offset;
        PdfPageViewModel page = _document.Pages[anchor.PageIndex];
        double x = geometry.PaddingLeft + ItemLeftInPanel(geometry, anchor.PageIndex, zoom)
                   + anchor.FractionX * PdfPageViewModel.PageWidthAt(page, zoom);
        double y = geometry.PaddingTop + ItemTopInPanel(anchor.PageIndex, zoom)
                   + anchor.FractionY * PdfPageViewModel.PageHeightAt(page, zoom);
        return new Point(x - offset.X, y - offset.Y);
    }

    private void ApplyState(double zoom, in ZoomAnchor anchor)
    {
#if DEBUG
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        ApplyStateCore(zoom, anchor);

        double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (elapsed > 15)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[zoom] layout commit {elapsed:F1} ms at zoom {zoom:F3} " +
                $"(extent {_scrollViewer.Extent.Width:F0}x{_scrollViewer.Extent.Height:F0})");
        }
#else
        ApplyStateCore(zoom, anchor);
#endif
    }

    private void ApplyStateCore(double zoom, in ZoomAnchor anchor)
    {
        Size viewport = Viewport;
        if (viewport.Width <= 0 || viewport.Height <= 0 || anchor.PageIndex >= _document.Pages.Count)
        {
            _document.Zoom = zoom;
            return;
        }

        _document.Zoom = zoom;
        double applied = _document.Zoom;

        // The ScrollViewer clamps a new offset against the extent it currently
        // knows about.  Run the pending layout first, otherwise a zoom step
        // that grows the document would silently lose part of the requested
        // scroll offset (and with it the pointer anchor).
        if (TopLevel.GetTopLevel(_scrollViewer) is not null) _scrollViewer.UpdateLayout();

        LayoutGeometry geometry = MeasureLayout(applied);

        double pageWidth = PdfPageViewModel.PageWidthAt(_document.Pages[anchor.PageIndex], applied);
        double pageHeight = PdfPageViewModel.PageHeightAt(_document.Pages[anchor.PageIndex], applied);
        double anchorX = geometry.PaddingLeft + ItemLeftInPanel(geometry, anchor.PageIndex, applied)
                         + anchor.FractionX * pageWidth;
        double anchorY = geometry.PaddingTop + ItemTopInPanel(anchor.PageIndex, applied)
                         + anchor.FractionY * pageHeight;

        // An axis is only anchored while it can actually be scrolled.  When the
        // document fits on an axis the offset has to stay 0, otherwise the
        // centred layout would end up de-centred with no scrollbar to pan back.
        // The hand-over is blended over a short band of scroll room so the
        // motion stays continuous (see AnchorHandoverBand).
        double maxOffsetX = Math.Max(0, geometry.ExtentWidth - viewport.Width);
        double baseLeft = geometry.PaddingLeft + ItemLeftInPanel(geometry, anchor.PageIndex, applied);
        double offsetX = 0;
        if (maxOffsetX > 0.1)
        {
            double anchoredOffset = Math.Clamp(anchorX - anchor.ViewportPoint.X, 0, maxOffsetX);
            double centredLeft = (viewport.Width - pageWidth) / 2;
            double anchoredLeft = baseLeft - anchoredOffset;
            double handover = Math.Clamp(maxOffsetX / AnchorHandoverBand, 0, 1);
            handover = handover * handover * (3 - (2 * handover));
            double blendedLeft = centredLeft + ((anchoredLeft - centredLeft) * handover);
            offsetX = Math.Clamp(baseLeft - blendedLeft, 0, maxOffsetX);
        }

        double maxOffsetY = Math.Max(0, geometry.ExtentHeight - viewport.Height);
        double offsetY = maxOffsetY > 0.5
            ? Math.Clamp(anchorY - anchor.ViewportPoint.Y, 0, maxOffsetY)
            : 0;

        var offset = new Vector(offsetX, offsetY);
        if (!_scrollViewer.Offset.Equals(offset)) _scrollViewer.Offset = offset;
    }

    // ── Content geometry (shared by capture and apply) ───────────────

    /// <summary>
    /// Content geometry at a given zoom factor.  Mirrors
    /// <see cref="PdfVirtualizingPanel"/>'s measure/arrange so the zoom
    /// controller can compute the exact target scroll position before the
    /// layout pass runs.
    /// </summary>
    private readonly record struct LayoutGeometry(
        double ViewportWidth,
        double ViewportHeight,
        double PaddingLeft,
        double PaddingTop,
        double PaddingRight,
        double PaddingBottom,
        double MaxItemWidth,
        double TotalItemHeight)
    {
        public double PanelWidth => Math.Max(ViewportWidth, MaxItemWidth + PaddingLeft + PaddingRight);

        public double ContentAreaWidth => Math.Max(0, PanelWidth - PaddingLeft - PaddingRight);

        public double ExtentWidth => PanelWidth;

        public double ExtentHeight => Math.Max(ViewportHeight, TotalItemHeight + PaddingTop + PaddingBottom);
    }

    private LayoutGeometry MeasureLayout(double zoom)
    {
        Thickness padding = _panel.Padding;
        Size viewport = Viewport;

        return new LayoutGeometry(
            viewport.Width,
            viewport.Height,
            padding.Left,
            padding.Top,
            padding.Right,
            padding.Bottom,
            _panel.MaxItemWidthAt(zoom),
            _panel.TotalItemHeightAt(zoom));
    }

    private double ItemLeftInPanel(LayoutGeometry geometry, int index, double zoom) =>
        PdfVirtualizingPanel.HorizontalItemOffset(
            geometry.ContentAreaWidth,
            PdfPageViewModel.ItemWidthAt(_document.Pages[index], zoom));

    private double ItemTopInPanel(int index, double zoom) =>
        _panel.ItemTopAt(index, zoom);

    private int FindPageIndex(double yInItems, double zoom) =>
        _panel.FindPageIndexAt(yInItems, zoom);

    // ── Easing ───────────────────────────────────────────────────────

    private static double FluentEase(double progress) =>
        CubicBezierEase(progress, EaseX1, EaseY1, EaseX2, EaseY2);

    /// <summary>
    /// Standard CSS-style cubic bezier easing with control points
    /// (0,0), (x1,y1), (x2,y2), (1,1).  Solved by bisection, which is more than
    /// accurate enough for a 200 ms animation.
    /// </summary>
    private static double CubicBezierEase(double progress, double x1, double y1, double x2, double y2)
    {
        if (progress <= 0) return 0;
        if (progress >= 1) return 1;

        double low = 0;
        double high = 1;
        double t = progress;
        for (int i = 0; i < 20; i++)
        {
            double x = BezierComponent(t, x1, x2);
            if (Math.Abs(x - progress) < 1e-6) break;
            if (x < progress) low = t;
            else high = t;
            t = (low + high) / 2;
        }

        return BezierComponent(t, y1, y2);
    }

    private static double BezierComponent(double t, double control1, double control2)
    {
        double inverse = 1 - t;
        return (3 * inverse * inverse * t * control1) + (3 * inverse * t * t * control2) + (t * t * t);
    }
}
