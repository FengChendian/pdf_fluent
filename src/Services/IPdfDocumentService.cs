using Avalonia.Media.Imaging;

namespace pdf_fluent.Services;

public interface IPdfDocumentService
{
    (double Width, double Height)[] GetPageSizes(string filePath);

    WriteableBitmap RenderPageToWriteableBitmap(
        string filePath,
        int pageNumber = 0,
        float scale = 1f,
        CancellationToken cancellationToken = default);

    void ReleaseDocument(string filePath);
}
