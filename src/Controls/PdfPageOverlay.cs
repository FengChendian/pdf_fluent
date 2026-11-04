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
    private static readonly IPen SelectedHighlightPen = new Pen(new SolidColorBrush(Color.FromArgb(230, 108, 92, 231)), 1.5);

    public static readonly StyledProperty<PdfPageViewModel?> PageProperty =
        AvaloniaProperty.Register<PdfPageOverlay, PdfPageViewModel?>(nameof(Page));

    public static readonly StyledProperty<DocumentTabViewModel?> DocumentProperty =
        AvaloniaProperty.Register<PdfPageOverlay, DocumentTabViewModel?>(nameof(Document));

    private PdfPageViewModel? _subscribedPage;
    private readonly Dictionary<Color, IBrush> _highlightBrushes = new();

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
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Page?.Text is not { } text || Document is not { } document) return;

        foreach (PdfHighlight highlight in document.GetHighlights(Page.PageIndex))
        {
            if (!_highlightBrushes.TryGetValue(highlight.Color, out IBrush? brush))
            {
                brush = new SolidColorBrush(highlight.Color);
                _highlightBrushes.Add(highlight.Color, brush);
            }
            IPen? pen = ReferenceEquals(document.SelectedHighlight, highlight) ? SelectedHighlightPen : null;
            foreach (PdfRect rect in highlight.Rects)
                DrawRect(context, brush, rect, text.CropBox, document.HighlightCornerRadius, pen);
        }

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
        return new Rect(
            (rect.L - cropBox.L) * scale,
            (cropBox.T - rect.T) * scale,
            rect.Width * scale,
            rect.Height * scale);
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
        double scale = DocumentTabViewModel.LayoutScale * Page!.Zoom;
        double radius = Math.Min(
            Math.Min(bounds.Width, bounds.Height) / 2,
            Math.Max(0, cornerRadius * scale));
        context.DrawRectangle(brush, pen, bounds, radius, radius);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SubscribeToPage();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_subscribedPage is not null) _subscribedPage.PropertyChanged -= OnPagePropertyChanged;
        _subscribedPage = null;
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
}
