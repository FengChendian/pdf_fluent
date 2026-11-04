using System.Diagnostics;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using Avalonia.Threading;
using pdf_fluent.ViewModels;

namespace pdf_fluent.Controls;

/// <summary>
/// Pointer-anchored, animated zoom for the virtualized page list.
///
/// <para>
/// The target layout and scroll offset are committed whenever the target
/// changes. Starting containers are retained while the target range is
/// realized, then the committed layout is compensated visually and the
/// compensation is animated away without further page measure, scroll, or
/// realization work on each frame.
/// </para>
/// <para>
/// The visual compensation is a uniform scale about the anchored page point,
/// animated through <see cref="Avalonia.Rendering.Composition.CompositionVisual"/>
/// (<c>Scale</c>/<c>CenterPoint</c>).  The interpolation therefore runs in the
/// compositor's render thread: a busy UI thread no longer delays the frames,
/// and the UI thread does not touch a render transform or re-render the page
/// subtree on every tick.  The UI thread only samples the logical clock for
/// the zoom percentage and commits the settlement.
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
/// A composition scale is outside the layout/hit-testing transform chain, so
/// while the animation runs pointer coordinates describe the committed layout
/// rather than the scaled pixels.  The controller maps a re-target anchor back
/// into that layout with its logical clock (see
/// <see cref="ToLayoutViewportPoint"/>); overlays use committed layout zoom because
/// the composition transform scales them with the page instead of applying zoom twice.
/// </para>
/// <para>
/// Rapid target changes do not restart the full 210 ms curve each time: a
/// retarget while a segment is still running uses a catch-up duration derived
/// from the interval between the two inputs.  Without it every new wheel notch
/// would restart the decelerate curve from zero velocity, so the faster the
/// wheel turned the less the page moved (and reversing direction looked
/// frozen).  The displayed segment advances only when the compositor confirms
/// the new batch, so input that arrives between frames is measured against
/// what is actually on screen instead of a fresh, not yet visible target.
/// </para>
/// <para>
/// Bitmaps are left untouched while the animation runs; the page keeps showing
/// its previous render (stretched to the new size) and is re-rendered once the
/// zoom settles, so the zoom never goes through a blank frame.  The composition
/// objects and the sampling timer are only referenced while a timeline is
/// active and are released on settlement, so a finished zoom cannot keep the
/// compositor, its animations, or the pages alive.
/// </para>
/// </summary>
internal sealed class PdfZoomController
{
    /// <summary>Fluent's point-to-point animation duration.</summary>
    private const double AnimationDurationMs = 210;

    /// <summary>
    /// Shortest catch-up animation used when a new wheel notch arrives while a
    /// previous one is still animating.  Restarting the full 210 ms curve for
    /// every notch low-passes rapid input (the faster the wheel, the less the
    /// page moves), so a retarget animates over a duration derived from the
    /// interval between the two inputs instead: isolated notches keep the
    /// Fluent feel, continuous input stays responsive.
    /// </summary>
    private const double MinRetargetDurationMs = 60;

    /// <summary>Retarget animations aim to finish shortly after the next input.</summary>
    private const double RetargetDurationFactor = 1.2;

    /// <summary>
    /// Settlement waits two extra 60 Hz frames after the current segment's
    /// duration so it cannot race the last render-thread frame and snap the
    /// scale to identity one frame too early.  The extra time is invisible: the
    /// animation already holds its final value.
    /// </summary>
    private const double SettleMarginMs = 34;

    /// <summary>How often the UI thread samples the logical animation clock.</summary>
    private const double SampleIntervalMs = 16;

    /// <summary>
    /// How long to wait for the compositor to confirm the commit before the
    /// logical clock starts on the local timestamp instead.  In a normal app
    /// the confirmation arrives with the next render frame; the timeout only
    /// covers windows that are not committing at all.
    /// </summary>
    private const double ClockStartTimeoutMs = 150;

    // Fluent "decelerate" interpolation (WinUI's cubic-bezier(0.1, 0.9, 0.2, 1.0)).
    private const double EaseX1 = 0.1;
    private const double EaseY1 = 0.9;
    private const double EaseX2 = 0.2;
    private const double EaseY2 = 1.0;

    /// <summary>Composition property animated by the zoom (see <see cref="CompositionVisual.Scale"/>).</summary>
    private const string ScaleProperty = "Scale";

    private static readonly Vector3D IdentityScale = new(1, 1, 1);

    /// <summary>
    /// Mirrors <see cref="FluentEase"/> on the render thread.  The same easing is
    /// attached to both key frames: the composition engine applies the easing of
    /// one end of the segment (Avalonia uses the destination key frame) and the
    /// first key frame's easing has no effect at progress 0, so this stays
    /// correct even if that detail changes.
    /// </summary>
    private static readonly SplineEasing FluentSplineEasing =
        new(EaseX1, EaseY1, EaseX2, EaseY2);

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
    private DispatcherTimer? _sampleTimer;

    /// <summary>
    /// The compositor the scale animation runs on.  Only kept while an
    /// animation is active so a detached panel/compositor cannot stay reachable
    /// through the controller.
    /// </summary>
    private Compositor? _compositor;
    private CompositionAnimation? _scaleAnimation;

    /// <summary>
    /// Identifies the current animation timeline.  Queued compositor
    /// confirmations and timer ticks from an older timeline compare against
    /// this value and do nothing.
    /// </summary>
    private long _animationGeneration;

    /// <summary>When the logical clock was requested / actually started.</summary>
    private long _clockRequestedAt;
    private long _clockStartedAt;

    /// <summary>
    /// True until the compositor confirms the batch that starts the animation.
    /// The first visible frame is the committed one, so the logical clock starts
    /// there instead of when <see cref="ZoomTo"/> was called.
    /// </summary>
    private bool _clockStartPending;

    /// <summary>
    /// Set by <see cref="Advance(TimeSpan)"/>: tests drive the logical clock and
    /// the dispatcher timer must not advance it concurrently.
    /// </summary>
    private bool _manualClock;

    /// <summary>Logical animation time in the manual (test) clock.</summary>
    private double _elapsedMs;
    private double _fromZoom;
    private double _targetZoom;
    private double _displayZoom = 1;

    /// <summary>Duration of the current visual segment (start -> target).</summary>
    private double _durationMs = AnimationDurationMs;

    // The segment that the render thread is actually showing.  A new target
    // starts a new composition animation immediately, but that animation only
    // replaces the visible one when the batch commits.  Until then retargeting
    // has to keep measuring against the *previous* segment, otherwise input that
    // arrives faster than the compositor commits would freeze the displayed
    // zoom at the last retarget's start value (and lose visible progress).
    private double _shownFrom;
    private double _shownTarget;
    private double _shownDurationMs;
    private long _shownStartedAt;
    private bool _shownValid;

    /// <summary>
    /// The composition scale origin (the page point the zoom is anchored at) in
    /// content coordinates.  Content coordinates are kept instead of viewport
    /// coordinates so re-targeting stays correct when the user scrolls while
    /// the animation runs.
    /// </summary>
    private Point _visualOrigin;

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

    /// <summary>
    /// The visual scale the current timeline would show right now: starting
    /// scale eased towards 1 over the current segment's duration (tests/DI).
    /// </summary>
    internal double CurrentVisualScale => IsAnimating && _targetZoom > 0
        ? CurrentDisplayZoom() / _targetZoom
        : 1;

    /// <summary>Composition scale origin in viewport coordinates (tests/DI).</summary>
    internal Point VisualOriginViewport => new(
        _visualOrigin.X - _scrollViewer.Offset.X,
        _visualOrigin.Y - _scrollViewer.Offset.Y);

    /// <summary>True while the composition animation is registered on the visual (tests/DI).</summary>
    internal bool HasCompositionAnimation => _scaleAnimation is not null;

    /// <summary>
    /// True once the logical clock runs from the compositor's commit instead of
    /// from <see cref="ZoomTo"/> (tests/DI).
    /// </summary>
    internal bool IsClockAlignedWithComposition =>
        IsAnimating && !_clockStartPending && !_manualClock;

    /// <summary>Duration of the current visual segment (tests/DI).</summary>
    internal double CurrentSegmentDurationMs => _durationMs;

    public void ZoomBy(double zoomDelta, Point? anchorViewportPoint)
    {
        // Accumulate same-direction input so fast wheel notches are not lost.
        // A queued target can be several steps ahead of the displayed zoom,
        // though; reversing must not unwind that queue while the page keeps
        // moving in the old direction.
        double basis = IsAnimating ? _targetZoom : _document.Zoom;
        double target = basis + zoomDelta;
        if (IsAnimating && zoomDelta != 0)
        {
            double displayedZoom = CurrentDisplayZoom();
            bool movesAgainstInput = zoomDelta > 0
                ? target <= displayedZoom
                : target >= displayedZoom;
            if (movesAgainstInput) target = displayedZoom + zoomDelta;
        }

        ZoomTo(target, anchorViewportPoint);
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
            // The pointer is over the *animated* visual state; map it back into
            // the committed layout before capturing the page point, otherwise
            // every re-target would capture a point that is off by the visual
            // compensation ('correction' is the current visual scale).
            double displayedZoom = CurrentDisplayZoom();
            double previousElapsed = CurrentElapsedMs();
            Point layoutAnchorPoint = ToLayoutViewportPoint(anchorPoint, displayedZoom);
            _anchor = CaptureAnchor(layoutAnchorPoint) with { ViewportPoint = anchorPoint };
            _fromZoom = displayedZoom;
            _durationMs = Math.Clamp(
                previousElapsed * RetargetDurationFactor,
                MinRetargetDurationMs,
                AnimationDurationMs);
        }
        else
        {
            _anchor = CaptureAnchor(anchorPoint);
            _fromZoom = _document.Zoom;
            _durationMs = AnimationDurationMs;
        }

        // A pending tick from the previous timeline must not touch the new one.
        CancelTimeline();

        _targetZoom = clamped;
        _elapsedMs = 0;
        _displayZoom = _fromZoom;
        IsAnimating = true;

        _document.SuspendRendering();
        _panel.BeginZoomAnimation();
        (double initialScale, Point origin) = CommitTargetLayout();

        if (!animate)
        {
            Settle();
            return;
        }

        if (Math.Abs(initialScale - 1) < 1e-6)
        {
            // The already displayed zoom is the new target (possible while a
            // previous animation is still running); the committed layout is
            // enough, there is nothing left to interpolate.
            Settle();
            return;
        }

        if (!StartVisualAnimation(initialScale, origin))
        {
            // No compositor (the panel is already detached): the layout commit
            // is all that is left to do.
            Settle();
            return;
        }

        StartTimeline();
    }

    /// <summary>Advances the animation by one frame. Exposed for deterministic tests.</summary>
    internal void Advance(TimeSpan delta)
    {
        if (!IsAnimating) return;

        _manualClock = true;
        _clockStartPending = false;
        _sampleTimer?.Stop();

        double deltaMs = delta.TotalMilliseconds;
        if (double.IsNaN(deltaMs) || deltaMs <= 0) return;

        _elapsedMs += deltaMs;
        if (_elapsedMs >= _durationMs)
        {
            _displayZoom = _targetZoom;
            Settle();
            return;
        }

        ApplyProgress(_elapsedMs);
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
        _animationGeneration++;
        _manualClock = false;
        _clockStartPending = false;
        StopSampleTimer();
        StopVisualAnimation();

        _displayZoom = _document.Zoom;
        _shownValid = false;
        _panel.EndZoomAnimation();
        _document.SetVisualZoom(null);
        _document.ResumeRendering();
    }

    // ── Composition animation ────────────────────────────────────────

    /// <summary>
    /// Starts the render-thread scale animation that compensates for the
    /// freshly committed layout.  The visual is scaled about the same page
    /// point the scroll offset was anchored at, starting at
    /// <paramref name="initialScale"/> and easing to identity.
    /// </summary>
    private bool StartVisualAnimation(double initialScale, Point origin)
    {
        if (ElementComposition.GetElementVisual(_panel) is not { } visual) return false;

        // Starting an animation for a property replaces the animation that is
        // currently attached to it on the render thread, so a target change
        // mid-animation does not need an explicit StopAnimation.
        var startScale = new Vector3D(initialScale, initialScale, 1);
        visual.CenterPoint = new Vector3D(origin.X, origin.Y, 0);
        visual.Scale = startScale;

        var animation = visual.Compositor.CreateVector3DKeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(_durationMs);
        // The animation already holds its final value; stopping it explicitly
        // therefore cannot snap the visual to an unexpected value.
        animation.StopBehavior = AnimationStopBehavior.SetToFinalValue;
        animation.InsertKeyFrame(0f, startScale, FluentSplineEasing);
        animation.InsertKeyFrame(1f, IdentityScale, FluentSplineEasing);
        visual.StartAnimation(ScaleProperty, animation);

        _compositor = visual.Compositor;
        _scaleAnimation = animation;
        _document.SetVisualZoom(_fromZoom);
        return true;
    }

    private void StopVisualAnimation()
    {
        _compositor = null;
        _scaleAnimation = null;

        // When the panel is detached its composition visual is gone with the
        // visual tree; there is nothing (and no compositor) left to restore.
        if (TopLevel.GetTopLevel(_panel) is null) return;
        if (ElementComposition.GetElementVisual(_panel) is not { } visual) return;

        visual.StopAnimation(ScaleProperty);
        visual.Scale = IdentityScale;
        visual.CenterPoint = default;
    }

    // ── Logical clock (zoom percentage + settlement) ─────────────────

    private void StartTimeline()
    {
        _clockStartPending = true;
        _manualClock = false;
        _clockRequestedAt = Stopwatch.GetTimestamp();
        _clockStartedAt = _clockRequestedAt;

        long generation = _animationGeneration;
        if (_compositor is { } compositor)
        {
            // Commit callbacks run on the UI thread right before the batch that
            // starts the animation is serialized.  Starting the logical clock
            // there keeps the percentage (and the correction used when the
            // target changes mid-animation) in step with the render thread.
            compositor.RequestCompositionUpdate(() => OnCompositionCommitted(generation));
        }
        else
        {
            _clockStartPending = false;
        }

        _sampleTimer ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(SampleIntervalMs),
            DispatcherPriority.Render,
            OnSampleTick);

        if (!_sampleTimer.IsEnabled) _sampleTimer.Start();
    }

    private void OnCompositionCommitted(long generation)
    {
        if (!IsAnimating || _manualClock || generation != _animationGeneration) return;
        if (!_clockStartPending) return;

        _clockStartedAt = Stopwatch.GetTimestamp();
        _clockStartPending = false;

        // From now on this segment is what the render thread shows.
        _shownFrom = _fromZoom;
        _shownTarget = _targetZoom;
        _shownDurationMs = _durationMs;
        _shownStartedAt = _clockStartedAt;
        _shownValid = true;
    }

    private void OnSampleTick(object? sender, EventArgs e)
    {
        if (!IsAnimating || _manualClock) return;

        long now = Stopwatch.GetTimestamp();
        if (_clockStartPending)
        {
            if (Stopwatch.GetElapsedTime(_clockRequestedAt, now).TotalMilliseconds < ClockStartTimeoutMs)
            {
                // The new target is not visible yet; keep sampling the segment
                // the user is actually looking at.
                if (_shownValid)
                    _document.SetVisualZoom(EvaluateSegment(
                        _shownFrom, _shownTarget, _shownDurationMs,
                        Stopwatch.GetElapsedTime(_shownStartedAt, now).TotalMilliseconds));
                return;
            }

            // The compositor never confirmed the commit (e.g. the window is not
            // producing frames).  Settle so rendering is resumed; the pending
            // animation is cancelled by StopVisualAnimation.
            Settle();
            return;
        }

        double elapsed = Stopwatch.GetElapsedTime(_clockStartedAt, now).TotalMilliseconds;
        if (elapsed >= _durationMs + SettleMarginMs)
        {
            _displayZoom = _targetZoom;
            Settle();
            return;
        }

        ApplyProgress(elapsed);
    }

    private void ApplyProgress(double elapsedMs)
    {
        _displayZoom = ZoomAt(elapsedMs);
        _document.SetVisualZoom(_displayZoom);
    }

    private double ZoomAt(double elapsedMs) =>
        EvaluateSegment(_fromZoom, _targetZoom, _durationMs, elapsedMs);

    private static double EvaluateSegment(double from, double target, double durationMs, double elapsedMs)
    {
        double progress = Math.Clamp(elapsedMs / durationMs, 0, 1);
        return from + ((target - from) * FluentEase(progress));
    }

    /// <summary>Elapsed time of the segment that is currently being displayed.</summary>
    private double CurrentElapsedMs()
    {
        if (!IsAnimating) return 0;
        if (_manualClock) return _elapsedMs;
        if (_clockStartPending) return 0;
        return Stopwatch.GetElapsedTime(_clockStartedAt).TotalMilliseconds;
    }

    private double CurrentDisplayZoom()
    {
        if (!IsAnimating) return _document.Zoom;
        if (_manualClock) return ZoomAt(_elapsedMs);

        if (_shownValid)
        {
            return EvaluateSegment(
                _shownFrom, _shownTarget, _shownDurationMs,
                Stopwatch.GetElapsedTime(_shownStartedAt).TotalMilliseconds);
        }

        return _fromZoom;
    }

    private void CancelTimeline()
    {
        _animationGeneration++;
        _manualClock = false;
        _clockStartPending = false;
        StopSampleTimer();
    }

    private void StopSampleTimer() => _sampleTimer?.Stop();

    private void Settle()
    {
        IsAnimating = false;
        _manualClock = false;
        _clockStartPending = false;
        _animationGeneration++;
        StopSampleTimer();
        StopVisualAnimation();

        _elapsedMs = _durationMs;
        _displayZoom = _targetZoom;
        _shownValid = false;
        _panel.EndZoomAnimation();
        if (TopLevel.GetTopLevel(_scrollViewer) is not null) _scrollViewer.UpdateLayout();
        _document.SetVisualZoom(null);
        _document.ResumeRendering();
    }

    private (double InitialScale, Point Origin) CommitTargetLayout()
    {
        ApplyState(_targetZoom, _anchor);
        if (TopLevel.GetTopLevel(_scrollViewer) is not null) _scrollViewer.UpdateLayout();

        Point targetAnchor = AnchorViewportPoint(_targetZoom, _anchor);
        double initialScale = _fromZoom / _targetZoom;
        double denominator = 1 - initialScale;
        Point originViewport = Math.Abs(denominator) < 1e-6
            ? _anchor.ViewportPoint
            : new Point(
                (_anchor.ViewportPoint.X - (initialScale * targetAnchor.X)) / denominator,
                (_anchor.ViewportPoint.Y - (initialScale * targetAnchor.Y)) / denominator);

        // The composition scale origin is expressed in the panel's coordinate
        // space, which is the scrolling content offset by the scroll offset.
        Vector offset = _scrollViewer.Offset;
        _visualOrigin = new Point(
            offset.X + originViewport.X,
            offset.Y + originViewport.Y);

        _panel.FreezeZoomViewport();
        return (initialScale, _visualOrigin);
    }

    /// <summary>
    /// Maps a viewport point of the currently displayed (visually scaled) state
    /// back into the committed layout, which is what
    /// <see cref="CaptureAnchor"/> measures page fractions in.
    /// </summary>
    private Point ToLayoutViewportPoint(Point viewportPoint, double displayedZoom)
    {
        double correction = displayedZoom / _document.Zoom;
        if (Math.Abs(correction - 1) < 1e-9) return viewportPoint;

        Vector offset = _scrollViewer.Offset;
        double originX = _visualOrigin.X;
        double originY = _visualOrigin.Y;
        double contentX = viewportPoint.X + offset.X;
        double contentY = viewportPoint.Y + offset.Y;

        return new Point(
            originX + ((contentX - originX) / correction) - offset.X,
            originY + ((contentY - originY) / correction) - offset.Y);
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
                   + (anchor.FractionX * PdfPageViewModel.PageWidthAt(page, zoom));
        double y = geometry.PaddingTop + ItemTopInPanel(anchor.PageIndex, zoom)
                   + (anchor.FractionY * PdfPageViewModel.PageHeightAt(page, zoom));
        return new Point(x - offset.X, y - offset.Y);
    }

    private void ApplyState(double zoom, in ZoomAnchor anchor)
    {
#if DEBUG
        long started = Stopwatch.GetTimestamp();
        ApplyStateCore(zoom, anchor);

        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
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
                         + (anchor.FractionX * pageWidth);
        double anchorY = geometry.PaddingTop + ItemTopInPanel(anchor.PageIndex, applied)
                         + (anchor.FractionY * pageHeight);

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
            PdfPageViewModel.PageWidthAt(_document.Pages[index], zoom));

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






