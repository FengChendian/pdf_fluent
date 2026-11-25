using Avalonia.Media.Imaging;
using pdf_fluent.Models;

namespace pdf_fluent.Services;

public interface IPdfDocumentService
{
    (double Width, double Height)[] GetPageSizes(string filePath);

    List<TocItem> GetBookmarks(string filePath);

    PdfTextPage GetPageText(
        string filePath,
        int pageNumber,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renders to the requested physical pixel size when supplied. The caller
    /// computes it before converting scale to float so layout and rendering
    /// share the same integer dimensions.
    /// </summary>
    WriteableBitmap RenderPageToWriteableBitmap(
        string filePath,
        int pageNumber = 0,
        float scale = 1f,
        CancellationToken cancellationToken = default,
        Avalonia.PixelSize? targetPixelSize = null);

    List<PdfHighlight> LoadHighlights(string filePath, int pageNumber);

    void SaveHighlightsToTemp(
        string filePath,
        IReadOnlyList<PdfHighlight> highlights,
        string tempPath,
        IReadOnlySet<int> modifiedPages);

    void ReleaseDocument(string filePath);
}
