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

    WriteableBitmap RenderPageToWriteableBitmap(
        string filePath,
        int pageNumber = 0,
        float scale = 1f,
        CancellationToken cancellationToken = default);

    List<PdfHighlight> LoadHighlights(string filePath, int pageNumber);

    void SaveHighlightsToTemp(
        string filePath,
        IReadOnlyList<PdfHighlight> highlights,
        string tempPath,
        IReadOnlySet<int> modifiedPages);

    void ReleaseDocument(string filePath);
}
