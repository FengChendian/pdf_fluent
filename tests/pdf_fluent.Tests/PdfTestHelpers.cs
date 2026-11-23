using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using pdf_fluent.ViewModels;
using Xunit;

namespace pdf_fluent.Tests;

internal static class PdfTestHelpers
{
    public static string SamplePdfPath()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "assets", "CUDA_by_Example.pdf");
        Assert.True(File.Exists(path), $"缺少测试样本：{Path.GetFullPath(path)}");
        return path;
    }

    public static long LiveBitmapBytes(DocumentTabViewModel document) =>
        document.Pages
            .Where(page => page.Image is not null)
            .Sum(page => (long)page.Image!.PixelSize.Width * page.Image.PixelSize.Height * 4);

    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(3));
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("等待异步操作完成超时。");
            await Task.Delay(10);
        }
    }

    public static T Named<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    public static Point Center(Control control, Visual relativeTo) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), relativeTo)!.Value;

    public static void Click(Window window, Control control)
    {
        Point center = Center(control, window);
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
    }
}
