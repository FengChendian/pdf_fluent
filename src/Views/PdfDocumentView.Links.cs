using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using pdf_fluent.Controls;
using pdf_fluent.Models;
using pdf_fluent.ViewModels;

namespace pdf_fluent.Views;

public partial class PdfDocumentView
{
    private static readonly Cursor LinkCursor = new(StandardCursorType.Hand);
    private PdfLink? _pressedLink;
    private int _pressedLinkPage = -1;
    private Point _linkPressPoint;
    private bool _linkClickCanceled;
    private IPointer? _linkPointer;
    private (DocumentTabViewModel Document, int Page, Point Point, double Zoom)? _linkHover;
    internal bool IsLinkCursor => ReferenceEquals(DocumentScroller.Cursor, LinkCursor);
    internal Func<Uri, Task<bool>>? LinkLauncher { get; set; }

    private void RememberLinkHover(PointerEventArgs e)
    {
        _linkHover = DataContext is DocumentTabViewModel document && IsPageContent(e.Source) &&
            TryGetPagePoint(e, out int pageIndex, out Point point)
            ? (document, pageIndex, point, document.Zoom) : null;
    }

    private void OnPageLinksLoaded(int pageIndex)
    {
        if (_linkHover is not { } hover || hover.Page != pageIndex || !ReferenceEquals(DataContext, hover.Document) ||
            hover.Document.Zoom != hover.Zoom || hover.Document.IsHighlightMode || _isSelectingText ||
            _zoomController is { IsAnimating: true } || hover.Document.HitTestHighlight(pageIndex, hover.Point) is not null) return;
        double scale = DocumentTabViewModel.LayoutScale * hover.Zoom;
        PdfLink? link = hover.Document.GetLoadedLinks(pageIndex)?.HitTestLink(hover.Point / scale);
        if (link is null) return;
        DocumentScroller.Cursor = LinkCursor;
        SetLinkStatus(link.Target!.DisplayText);
    }

    private bool TryGetLink(PointerEventArgs e, out int pageIndex, out PdfLink? link)
    {
        link = null;
        pageIndex = -1;
        if (!IsPageContent(e.Source) && _linkPointer is null) return false;
        if (DataContext is not DocumentTabViewModel document || document.IsHighlightMode ||
            _zoomController is { IsAnimating: true } || !TryGetPagePoint(e, out pageIndex, out Point point)) return false;
        PdfLinkPage? links = document.GetLoadedLinks(pageIndex);
        if (links is null) return false;
        double scale = DocumentTabViewModel.LayoutScale * document.Zoom;
        if (document.HitTestHighlight(pageIndex, point) is not null) return false;
        link = links.HitTestLink(point / scale);
        return link is not null;
    }

    private void SetLinkStatus(string? status)
    {
        LinkStatusText.Text = status;
        LinkStatus.IsVisible = !string.IsNullOrEmpty(status);
    }

    private void CancelLinkPress()
    {
        _pressedLink = null;
        _pressedLinkPage = -1;
        _linkClickCanceled = false;
        IPointer? pointer = _linkPointer;
        _linkPointer = null;
        // Text selection owns the capture when the link also covers text.
        if (!_isSelectingText && pointer?.Captured == DocumentScroller) pointer.Capture(null);
    }

    private async Task ActivateLinkAsync(PdfLinkTarget target, DocumentTabViewModel document)
    {
        if (target.Destination is { } destination)
        {
            NavigateToLinkDestination(destination);
            return;
        }
        if (target.Uri is not { } uri || PdfLinkTarget.FromUri(uri.AbsoluteUri) is null) return;
        try
        {
            bool launched = LinkLauncher is { } launcher
                ? await launcher(uri)
                : TopLevel.GetTopLevel(this) is { } top && await top.Launcher.LaunchUriAsync(uri);
            if (!launched && ReferenceEquals(DataContext, document)) SetLinkStatus("无法打开链接");
        }
        catch (Exception)
        {
            if (ReferenceEquals(DataContext, document)) SetLinkStatus("无法打开链接");
        }
    }

    internal void NavigateToLinkDestination(PdfDestination destination)
    {
        if (DataContext is not DocumentTabViewModel document || destination.PageIndex < 0 ||
            destination.PageIndex >= document.Pages.Count) return;
        PdfVirtualizingPanel? panel = PageItems.GetVisualDescendants().OfType<PdfVirtualizingPanel>().FirstOrDefault();
        if (panel is null) return;
        _zoomController?.FinishAnimation();
        ClosePageJump();
        _scrollOffsetToRestore = null;
        double oldZoom = document.Zoom;
        double oldScale = DocumentTabViewModel.LayoutScale * oldZoom;
        int sourcePage = Math.Clamp(document.CurrentPage - 1, 0, document.Pages.Count - 1);
        double preservedY = Math.Max(0, DocumentScroller.Offset.Y - panel.Padding.Top - panel.ItemTopAt(sourcePage, oldZoom)) / oldScale;
        PdfPageViewModel page = document.Pages[destination.PageIndex];
        double width = Math.Max(1, DocumentScroller.Viewport.Width - panel.Padding.Left - panel.Padding.Right);
        double height = Math.Max(1, DocumentScroller.Viewport.Height - panel.Padding.Top - panel.Padding.Bottom);
        double zoom = destination.View switch
        {
            PdfDestinationView.Fit or PdfDestinationView.FitB => Math.Min(width / page.BaseWidth, height / page.BaseHeight),
            PdfDestinationView.FitH or PdfDestinationView.FitBH => width / page.BaseWidth,
            PdfDestinationView.FitV or PdfDestinationView.FitBV => height / page.BaseHeight,
            _ => destination.Zoom ?? oldZoom,
        };
        Rect? fitBounds = null;
        if (destination.FitRect is { Width: > 0, Height: > 0 } rect)
        {
            Point first = PdfLinkCoordinates.ToPage(new Point(rect.L, rect.T), destination.PageBox, destination.Rotation);
            Point last = PdfLinkCoordinates.ToPage(new Point(rect.R, rect.B), destination.PageBox, destination.Rotation);
            fitBounds = new Rect(first, last).Normalize();
            zoom = Math.Min(width / fitBounds.Value.Width, height / fitBounds.Value.Height) / DocumentTabViewModel.LayoutScale;
        }
        document.Zoom = double.IsFinite(zoom) ? Math.Clamp(zoom, DocumentTabViewModel.MinZoom, DocumentTabViewModel.MaxZoom) : oldZoom;
        PageItems.UpdateLayout();
        double scale = DocumentTabViewModel.LayoutScale * document.Zoom;
        double panelWidth = Math.Max(DocumentScroller.Viewport.Width,
            panel.MaxItemWidthAt(document.Zoom) + panel.Padding.Left + panel.Padding.Right);
        double pageLeft = panel.Padding.Left + PdfVirtualizingPanel.HorizontalItemOffset(
            panelWidth - panel.Padding.Left - panel.Padding.Right, page.PageWidth);
        Point target = PdfLinkCoordinates.ToPage(
            new Point(destination.X ?? destination.PageBox.L, destination.Y ?? destination.PageBox.T),
            destination.PageBox, destination.Rotation);
        bool rotated = (destination.Rotation & 1) != 0;
        bool hasHorizontal = rotated ? destination.Y.HasValue : destination.X.HasValue;
        bool hasVertical = rotated ? destination.X.HasValue : destination.Y.HasValue;
        bool fitPage = destination.View is PdfDestinationView.Fit or PdfDestinationView.FitB;
        double x = hasHorizontal ? pageLeft + target.X * scale : DocumentScroller.Offset.X;
        double y = hasVertical ? target.Y : destination.View == PdfDestinationView.Xyz ? preservedY : 0;
        if (fitPage) { x = 0; y = 0; }
        if (fitBounds is { } bounds) { x = pageLeft + bounds.X * scale; y = bounds.Y; }
        _pendingPageIndex = null;
        document.CurrentPage = destination.PageIndex + 1;
        DocumentScroller.Offset = new Vector(Math.Max(0, x), Math.Max(0,
            panel.Padding.Top + panel.ItemTopAt(destination.PageIndex, document.Zoom) + y * scale));
        document.UpdateScrollOffset(DocumentScroller.Offset);
        SetLinkStatus(null);
        DocumentScroller.Cursor = DefaultCursor;
    }
}
