using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using pdf_fluent.Models;
using pdf_fluent.ViewModels;

namespace pdf_fluent.Controls;

/// <summary>
/// Draws the complete page into a device-pixel rectangle, without Image's
/// aspect-ratio fitting step rescaling an already rasterized page a second time.
/// </summary>
public sealed class PdfPageImage : Control
{
    public static readonly StyledProperty<PdfPageViewModel?> PageProperty =
        AvaloniaProperty.Register<PdfPageImage, PdfPageViewModel?>(nameof(Page));
    public static readonly StyledProperty<DocumentTabViewModel?> DocumentProperty =
        AvaloniaProperty.Register<PdfPageImage, DocumentTabViewModel?>(nameof(Document));

    private PdfPageViewModel? _subscribedPage;
    private DocumentTabViewModel? _subscribedDocument;
    private TopLevel? _topLevel;
    private PdfPageGeometry _geometry;

    public PdfPageImage() => LayoutUpdated += OnLayoutUpdated;

    static PdfPageImage()
    {
        PageProperty.Changed.AddClassHandler<PdfPageImage>((image, _) => image.Subscribe());
        DocumentProperty.Changed.AddClassHandler<PdfPageImage>((image, _) => image.Subscribe());
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

    internal event EventHandler? GeometryChanged;

    internal PdfPageGeometry Geometry
    {
        get { UpdateGeometry(); return _geometry; }
    }

    internal BitmapInterpolationMode InterpolationMode
    {
        get
        {
            if (Page?.Image is not { } bitmap || Document?.IsZoomAnimating == true)
                return BitmapInterpolationMode.LowQuality;

            PixelSize target = Geometry.PixelSize;
            if (bitmap.PixelSize == target) return BitmapInterpolationMode.None;
            return bitmap.PixelSize.Width >= target.Width && bitmap.PixelSize.Height >= target.Height
                ? BitmapInterpolationMode.MediumQuality
                : BitmapInterpolationMode.LowQuality;
        }
    }

    public override void Render(DrawingContext context)
    {
        if (Page is null) return;
        Rect destination = Geometry.Bounds;
        // Own the page clip so snapping by a fraction of a DIP is not clipped
        // again by the surrounding logical page surface.
        using (context.PushClip(new RoundedRect(destination, 2)))
        {
            context.DrawRectangle(Brushes.White, null, destination);
            if (Page.Image is not { } bitmap) return;
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = InterpolationMode }))
                context.DrawImage(bitmap, new Rect(bitmap.Size), destination);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null) _topLevel.ScalingChanged += OnScalingChanged;
        Subscribe();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Unsubscribe();
        if (_topLevel is not null) _topLevel.ScalingChanged -= OnScalingChanged;
        _topLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void Subscribe()
    {
        Unsubscribe();
        if (_topLevel is not null)
        {
            _subscribedPage = Page;
            _subscribedDocument = Document;
            if (_subscribedPage is not null) _subscribedPage.PropertyChanged += OnPageChanged;
            if (_subscribedDocument is not null) _subscribedDocument.PropertyChanged += OnDocumentChanged;
        }
        UpdateGeometry();
        InvalidateVisual();
    }

    private void Unsubscribe()
    {
        if (_subscribedPage is not null) _subscribedPage.PropertyChanged -= OnPageChanged;
        if (_subscribedDocument is not null) _subscribedDocument.PropertyChanged -= OnDocumentChanged;
        _subscribedPage = null;
        _subscribedDocument = null;
    }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PdfPageViewModel.PageWidth) or nameof(PdfPageViewModel.PageHeight))
            UpdateGeometry();
        if (e.PropertyName == nameof(PdfPageViewModel.Image)) InvalidateVisual();
    }

    private void OnDocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Do not invalidate the page on VisualZoom's animation ticks: the
        // compositor scales the recorded page without redrawing its subtree.
        if (e.PropertyName == nameof(DocumentTabViewModel.IsZoomAnimating)) InvalidateVisual();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => UpdateGeometry();
    private void OnScalingChanged(object? sender, EventArgs e) => UpdateGeometry();

    private void UpdateGeometry()
    {
        if (Page is not { } page) return;
        Point origin = _topLevel is null ? default : this.TranslatePoint(default, _topLevel) ?? default;
        var geometry = PdfPageGeometry.Create(
            new Size(page.PageWidth, page.PageHeight), _topLevel?.RenderScaling ?? 1, origin);
        if (_geometry == geometry) return;
        _geometry = geometry;
        GeometryChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }
}
