using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace pdf_fluent.ViewModels;

public sealed partial class PdfPageViewModel : ViewModelBase
{
    private const double PageFooterHeight = 33;
    private readonly double _width;
    private readonly double _height;

    public PdfPageViewModel(int pageIndex, double width, double height, double renderScale)
    {
        PageIndex = pageIndex;
        _width = width;
        _height = height;
        SetLayout(width, height, renderScale);
    }

    public int PageIndex { get; }
    public int PageNumber => PageIndex + 1;
    public double PageWidth { get; private set; }
    public double PageHeight { get; private set; }
    public double ItemWidth => PageWidth;
    public double ItemHeight => PageHeight + PageFooterHeight;

    [ObservableProperty]
    private WriteableBitmap? _image;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    public bool HasError => ErrorMessage is not null;

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    public void SetRenderScale(double renderScale) => SetLayout(_width, _height, renderScale);

    private void SetLayout(double width, double height, double renderScale)
    {
        PageWidth = Math.Max(1, width * renderScale);
        PageHeight = Math.Max(1, height * renderScale);
        OnPropertyChanged(nameof(PageWidth));
        OnPropertyChanged(nameof(PageHeight));
        OnPropertyChanged(nameof(ItemWidth));
        OnPropertyChanged(nameof(ItemHeight));
    }

    public void ClearImage()
    {
        Image?.Dispose();
        Image = null;
    }
}
