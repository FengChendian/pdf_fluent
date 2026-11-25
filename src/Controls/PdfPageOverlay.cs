using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using pdf_fluent.Models;
using pdf_fluent.ViewModels;

namespace pdf_fluent.Controls;

public sealed class PdfPageOverlay : Control
{
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromArgb(76, 55, 116, 235));
    private static readonly IBrush SearchBrush = new SolidColorBrush(Color.FromArgb(88, 255, 193, 7));

    public static readonly StyledProperty<PdfPageViewModel?> PageProperty =
        AvaloniaProperty.Register<PdfPageOverlay, PdfPageViewModel?>(nameof(Page));

    public static readonly StyledProperty<DocumentTabViewModel?> DocumentProperty =
        AvaloniaProperty.Register<PdfPageOverlay, DocumentTabViewModel?>(nameof(Document));

    public static readonly StyledProperty<PdfPageImage?> PageImageProperty =
        AvaloniaProperty.Register<PdfPageOverlay, PdfPageImage?>(nameof(PageImage));

    private PdfPageViewModel? _subscribedPage;
    private PdfPageImage? _subscribedImage;

    public PdfPageImage? PageImage
    {
        get => GetValue(PageImageProperty);
        set => SetValue(PageImageProperty, value);
    }

    public PdfPageViewModel? Page
    {
        get => GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    public DocumentTabViewModel? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    static PdfPageOverlay()
    {
        PageProperty.Changed.AddClassHandler<PdfPageOverlay>((overlay, _) => overlay.SubscribeToPage());
        DocumentProperty.Changed.AddClassHandler<PdfPageOverlay>((overlay, _) => overlay.InvalidateVisual());
        PageImageProperty.Changed.AddClassHandler<PdfPageOverlay>((overlay, _) => overlay.SubscribeToImage());
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Page?.Text is not { } text || Document is not { } document) return;

        var highlights = new List<PdfHighlightDrawItem>();
        foreach (PdfHighlight highlight in document.GetHighlights(Page.PageIndex))
        {
            bool selected = ReferenceEquals(document.SelectedHighlight, highlight);
            foreach (PdfRect rect in highlight.Rects)
            {
                Rect bounds = GetLayoutBounds(rect);
                highlights.Add(new PdfHighlightDrawItem(
                    bounds,
                    highlight.Color,
                    GetCornerRadius(bounds, document.HighlightCornerRadius),
                    selected));
            }
        }
        if (highlights.Count > 0)
            context.Custom(new PdfHighlightDrawOperation(highlights, new Rect(Bounds.Size)));

        IBrush selectionBrush = document.ActiveSearchResult?.PageIndex == Page.PageIndex
            ? SearchBrush
            : SelectionBrush;
        foreach (PdfRect rect in document.GetSelectionRects(Page.PageIndex))
            DrawRect(context, selectionBrush, rect, text.CropBox, 1.5);
    }

    internal Rect GetLayoutBounds(PdfRect rect)
    {
        PdfRect cropBox = Page!.Text!.CropBox;
        double scale = DocumentTabViewModel.LayoutScale * Page.Zoom;
        var logical = new Rect(
            (rect.L - cropBox.L) * scale,
            (cropBox.T - rect.T) * scale,
            rect.Width * scale,
            rect.Height * scale);
        return PageImage is { } image ? image.Geometry.FromLogical(logical) : logical;
    }

    private void DrawRect(
        DrawingContext context,
        IBrush brush,
        PdfRect rect,
        PdfRect cropBox,
        double cornerRadius,
        IPen? pen = null)
    {
        Rect bounds = GetLayoutBounds(rect);
        double radius = GetCornerRadius(bounds, cornerRadius);
        context.DrawRectangle(brush, pen, bounds, radius, radius);
    }

    private double GetCornerRadius(Rect bounds, double cornerRadius)
    {
        double scale = DocumentTabViewModel.LayoutScale * Page!.Zoom;
        return Math.Min(
            Math.Min(bounds.Width, bounds.Height) / 2,
            Math.Max(0, cornerRadius * scale));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SubscribeToPage();
        SubscribeToImage();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_subscribedPage is not null) _subscribedPage.PropertyChanged -= OnPagePropertyChanged;
        _subscribedPage = null;
        if (_subscribedImage is not null) _subscribedImage.GeometryChanged -= OnGeometryChanged;
        _subscribedImage = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void SubscribeToPage()
    {
        if (_subscribedPage is not null) _subscribedPage.PropertyChanged -= OnPagePropertyChanged;
        _subscribedPage = Page;
        if (_subscribedPage is not null) _subscribedPage.PropertyChanged += OnPagePropertyChanged;
        InvalidateVisual();
    }

    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PdfPageViewModel.Text) or nameof(PdfPageViewModel.TextOverlayVersion))
            InvalidateVisual();
    }

    private void SubscribeToImage()
    {
        if (_subscribedImage is not null) _subscribedImage.GeometryChanged -= OnGeometryChanged;
        _subscribedImage = PageImage;
        if (_subscribedImage is not null) _subscribedImage.GeometryChanged += OnGeometryChanged;
        InvalidateVisual();
    }

    private void OnGeometryChanged(object? sender, EventArgs e) => InvalidateVisual();
}
