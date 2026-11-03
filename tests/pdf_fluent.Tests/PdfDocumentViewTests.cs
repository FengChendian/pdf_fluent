using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
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

    [AvaloniaFact]
    public async Task ZoomControlsAreCenteredAndEvenlySpacedWithoutRenderingLabels()
    {
        using var service = new FakePdfDocumentService(pageCount: 1);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();

        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1200, Height = 900, Content = view };

        try
        {
            window.Show();
            window.UpdateLayout();

            Grid zoomControls = view.GetVisualDescendants()
                .OfType<Grid>()
                .Single(grid => grid.Name == "ZoomControls");
            Button zoomOut = zoomControls.Children.OfType<Button>().Single(button => button.Name == "ZoomOutButton");
            Button zoomIn = zoomControls.Children.OfType<Button>().Single(button => button.Name == "ZoomInButton");
            TextBlock percentage = zoomControls.Children.OfType<TextBlock>().Single();
            double[] centers = new Control[] { zoomOut, percentage, zoomIn }
                .Select(control => control.TranslatePoint(
                    new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), zoomControls)!.Value.X)
                .ToArray();

            Assert.Equal(44, centers[1] - centers[0], precision: 1);
            Assert.Equal(44, centers[2] - centers[1], precision: 1);
            Assert.Equal(66, (centers[0] + centers[2]) / 2, precision: 1);

            Point zoomOutCenter = zoomOut.TranslatePoint(
                new Point(zoomOut.Bounds.Width / 2, zoomOut.Bounds.Height / 2), window)!.Value;
            window.MouseMove(zoomOutCenter);
            Assert.Equal(
                Avalonia.Media.Color.Parse("#F0F3F9"),
                Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(zoomOut.Background).Color);
            Avalonia.Controls.Presenters.ContentPresenter zoomOutSurface = zoomOut.GetVisualChildren()
                .OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                .Single();
            Assert.Equal(
                Avalonia.Media.Color.Parse("#F0F3F9"),
                Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(zoomOutSurface.Background).Color);

            var minus = Assert.IsType<Border>(zoomOut.Content);
            Assert.Equal(10, minus.Width);
            Assert.True(minus.Height > 0);
            Assert.NotNull(minus.Background);

            foreach (Button button in new[] { zoomOut, zoomIn })
            {
                var icon = Assert.IsAssignableFrom<Control>(button.Content);
                Point iconCenter = icon.TranslatePoint(
                    new Point(icon.Bounds.Width / 2, icon.Bounds.Height / 2), button)!.Value;
                Assert.Equal(button.Bounds.Width / 2, iconCenter.X, precision: 1);
                Assert.Equal(button.Bounds.Height / 2, iconCenter.Y, precision: 1);
            }

            string?[] labels = view.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToArray();
            Assert.DoesNotContain("连续滚动", labels);
            Assert.DoesNotContain("按需渲染", labels);
            Assert.DoesNotContain("页面将在滚动时按需渲染", labels);
        }
        finally
        {
            window.Close();
        }
    }
}
