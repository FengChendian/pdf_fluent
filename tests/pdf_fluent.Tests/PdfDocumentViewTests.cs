using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using pdf_fluent.Services;
using pdf_fluent.ViewModels;
using pdf_fluent.Views;
using Xunit;

namespace pdf_fluent.Tests;

public sealed class PdfDocumentViewTests
{
    [AvaloniaFact]
    public void CtrlWheelChangesZoomWhilePlainWheelDoesNot()
    {
        using var pdfService = new PDFiumService();
        using var document = new DocumentTabViewModel("unused.pdf", pdfService)
        {
            IsLoading = false,
        };
        var window = new Window
        {
            Width = 1200,
            Height = 900,
            Content = new PdfDocumentView { DataContext = document },
        };

        try
        {
            window.Show();
            window.MouseWheel(new Point(500, 500), new Vector(0, 1), RawInputModifiers.None);
            Assert.Equal(100, document.ZoomPercent);

            window.MouseWheel(new Point(500, 500), new Vector(0, 1), RawInputModifiers.Control);
            Assert.Equal(125, document.ZoomPercent);

            window.MouseWheel(new Point(500, 500), new Vector(0, -1), RawInputModifiers.Control);
            Assert.Equal(100, document.ZoomPercent);
        }
        finally
        {
            window.Close();
        }
    }
}
