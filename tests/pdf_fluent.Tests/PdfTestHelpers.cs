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
}
