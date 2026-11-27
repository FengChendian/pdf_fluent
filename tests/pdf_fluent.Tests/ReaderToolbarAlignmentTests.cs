using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using pdf_fluent.ViewModels;
using pdf_fluent.Views;
using Xunit;
using static pdf_fluent.Tests.PdfTestHelpers;

namespace pdf_fluent.Tests;

/// <summary>
/// 阅读器头部是一整排 34 DIP 高的控件，三个弹层都按同一条规则挂在头部下沿下方
/// （<c>Margin="0,41,..."</c>，见 PdfDocumentView.axaml）。因此弹层缺口到锚点的间距
/// 只取决于锚点底边：只要这一排里有一个控件矮 2 DIP，它的缺口就会远 2 DIP。
/// 这两个测试把“整排同高”和“缺口等距”固定下来。
/// </summary>
public sealed class ReaderToolbarAlignmentTests
{
    [AvaloniaFact]
    public async Task HeaderToolButtonsShareThePageNavigationHeight()
    {
        using var service = new FakePdfDocumentService(pageCount: 3);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1200, Height = 900, Content = view };

        try
        {
            window.Show();
            window.UpdateLayout();

            var header = Named<Border>(view, "ReaderHeader");
            var navigation = Named<Border>(view, "PageNavigation");
            double expectedBottom = Bottom(navigation, view);

            Button[] tools = header.GetVisualDescendants()
                .OfType<Button>()
                .Where(button => button.Classes.Contains("tool-button") && button.IsVisible)
                .ToArray();

            Assert.NotEmpty(tools);
            foreach (Button tool in tools)
            {
                // 一排 34 DIP 的正方形按钮，与页码导航同高：底边齐平后，弹层缺口到
                // 每个锚点的间距才一致（见下面的缺口等距测试）。
                Assert.Equal(navigation.Bounds.Height, tool.Bounds.Height, precision: 1);
                Assert.Equal(tool.Bounds.Height, tool.Bounds.Width, precision: 1);
                Assert.Equal(expectedBottom, Bottom(tool, view), precision: 1);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task FlyoutNotchesSitTheSameDistanceBelowTheirAnchors()
    {
        using var service = new FakePdfDocumentService(pageCount: 3);
        using var document = new DocumentTabViewModel("unused.pdf", service);
        await document.LoadAsync();
        var view = new PdfDocumentView { DataContext = document };
        var window = new Window { Width = 1200, Height = 900, Content = view };

        try
        {
            window.Show();
            window.UpdateLayout();

            var navigation = Named<Border>(view, "PageNavigation");
            var pen = view.HighlightButton;
            var palette = Named<Grid>(view, "HighlightPaletteOverlay");
            var jump = Named<Grid>(view, "PageJumpOverlay");

            Click(window, pen);
            await Task.Delay(300);
            window.UpdateLayout();
            Assert.True(palette.IsVisible);
            double paletteGap = palette.TranslatePoint(default, view)!.Value.Y - Bottom(pen, view);

            Click(window, pen);
            await Task.Delay(300);
            Click(window, Named<TextBlock>(view, "PageIndicatorText"));
            await Task.Delay(300);
            window.UpdateLayout();
            Assert.True(jump.IsVisible);
            double jumpGap = jump.TranslatePoint(default, view)!.Value.Y - Bottom(navigation, view);

            // 两个缺口顶点默认都在（0,0），所以弹层顶边即缺口顶点。
            Assert.Equal(1, paletteGap, precision: 1);
            Assert.Equal(1, jumpGap, precision: 1);
            Assert.Equal(jumpGap, paletteGap, precision: 1);
        }
        finally { window.Close(); }
    }

    private static double Bottom(Control control, Visual relativeTo) =>
        control.TranslatePoint(default, relativeTo)!.Value.Y + control.Bounds.Height;
}
