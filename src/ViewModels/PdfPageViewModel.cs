using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using pdf_fluent.Models;

namespace pdf_fluent.ViewModels;

public sealed partial class PdfPageViewModel : ViewModelBase
{
    /// <summary>Vertical spacing below a page in the continuous document flow.</summary>
    public const double FooterHeight = 16.5;

    private readonly double _baseWidth;
    private readonly double _baseHeight;
    private double _zoom;
    private WriteableBitmap? _image;
    private PdfTextPage? _text;

    public PdfPageViewModel(int pageIndex, double widthInPoints, double heightInPoints, double zoom)
    {
        PageIndex = pageIndex;
        PointWidth = Math.Max(1, widthInPoints);
        PointHeight = Math.Max(1, heightInPoints);
        _baseWidth = PointWidth * DocumentTabViewModel.LayoutScale;
        _baseHeight = PointHeight * DocumentTabViewModel.LayoutScale;
        _zoom = Math.Max(0.01, zoom);
    }

    public int PageIndex { get; }

    public int PageNumber => PageIndex + 1;

    /// <summary>Page width in PDF points (1/72 inch).</summary>
    public double PointWidth { get; }

    /// <summary>Page height in PDF points (1/72 inch).</summary>
    public double PointHeight { get; }

    /// <summary>Page width in layout units at 100% zoom.</summary>
    public double BaseWidth => _baseWidth;

    /// <summary>Page height in layout units at 100% zoom.</summary>
    public double BaseHeight => _baseHeight;

    /// <summary>Zoom factor the page is currently laid out with.</summary>
    public double Zoom => _zoom;

    public double PageWidth => PageWidthAt(this, _zoom);

    public double PageHeight => PageHeightAt(this, _zoom);

    public double ItemWidth => PageWidth;

    public double ItemHeight => ItemHeightAt(this, _zoom);

    /// <summary>
    /// Render scale (device pixels per PDF point) the bitmap currently in
    /// <see cref="Image"/> was produced with. <see cref="double.NaN"/> when
    /// there is no bitmap.
    /// </summary>
    public double RenderedScale { get; private set; } = double.NaN;

    public PdfTextPage? Text
    {
        get => _text;
        private set
        {
            if (ReferenceEquals(_text, value)) return;
            _text = value;
            OnPropertyChanged();
            NotifyTextOverlayChanged();
        }
    }

    public int TextOverlayVersion { get; private set; }

    /// <summary>
    /// Current bitmap. It stays in place while the page is re-rendered for a
    /// new zoom factor, so zooming never blanks the page.
    /// </summary>
    public WriteableBitmap? Image
    {
        get => _image;
        private set
        {
            WriteableBitmap? previous = _image;
            if (ReferenceEquals(previous, value)) return;

            // Publish the new bitmap before releasing the old one: the
            // Image control drops its reference to the previous bitmap as
            // part of the property-change notification.
            _image = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPlaceholderVisible));
            previous?.Dispose();
        }
    }

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    public bool HasError => ErrorMessage is not null;

    /// <summary>
    /// The loading placeholder is only shown for pages that have nothing to
    /// display yet; re-rendering an already visible page keeps the previous
    /// bitmap on screen instead of flashing a spinner.
    /// </summary>
    public bool IsPlaceholderVisible => IsLoading && _image is null;

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsPlaceholderVisible));

    /// <summary>Updates the layout zoom. Notifications are optional because a
    /// zoom animation only needs to notify the pages that are on screen.</summary>
    public void SetZoom(double zoom, bool notify)
    {
        if (_zoom.Equals(zoom)) return;
        _zoom = zoom;
        if (notify) NotifyLayoutChanged();
    }

    public void NotifyLayoutChanged()
    {
        OnPropertyChanged(nameof(Zoom));
        OnPropertyChanged(nameof(PageWidth));
        OnPropertyChanged(nameof(PageHeight));
        OnPropertyChanged(nameof(ItemWidth));
        OnPropertyChanged(nameof(ItemHeight));
        NotifyTextOverlayChanged();
    }

    public void SetText(PdfTextPage text) => Text = text;

    public PdfLinkPage? Links { get; private set; }

    public void SetLinks(PdfLinkPage links)
    {
        Links = links;
        OnPropertyChanged(nameof(Links));
    }

    public void NotifyTextOverlayChanged()
    {
        TextOverlayVersion++;
        OnPropertyChanged(nameof(TextOverlayVersion));
    }

    public void SetImage(WriteableBitmap bitmap, double renderedScale)
    {
        RenderedScale = renderedScale;
        Image = bitmap;
    }

    public void ClearImage()
    {
        RenderedScale = double.NaN;
        Image = null;
    }

    // ── Geometry helpers shared by the panel and the zoom controller ──

    public static double PageWidthAt(PdfPageViewModel page, double zoom) =>
        Math.Max(1, page._baseWidth * zoom);

    public static double PageHeightAt(PdfPageViewModel page, double zoom) =>
        Math.Max(1, page._baseHeight * zoom);

    public static double ItemHeightAt(PdfPageViewModel page, double zoom) =>
        PageHeightAt(page, zoom) + FooterHeight;
}
