using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using pdf_fluent.ViewModels;
using pdf_fluent.Views;
using Xunit;

namespace pdf_fluent.Tests;

public sealed class PdfDocumentViewTests
{
    [AvaloniaFact]
    public async Task CtrlWheelChangesZoomWhilePlainWheelDoesNot()
    {
        using var service = new FakePdfDocumentService(pageCount: 3);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();

        var view = new PdfDocumentView { DataContext = document };
        var window = new Window
        {
            Width = 1200,
            Height = 900,
            Content = view,
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            window.MouseWheel(new Point(500, 500), new Vector(0, 1), RawInputModifiers.None);
            Assert.Equal(100, document.ZoomPercent);

            window.MouseWheel(new Point(500, 500), new Vector(0, 1), RawInputModifiers.Control);
            view.ZoomController!.FinishAnimation();
            Assert.Equal(125, document.ZoomPercent);

            window.MouseWheel(new Point(500, 500), new Vector(0, -1), RawInputModifiers.Control);
            view.ZoomController!.FinishAnimation();
            Assert.Equal(100, document.ZoomPercent);
        }
        finally
        {
            window.Close();
        }
    }
}
