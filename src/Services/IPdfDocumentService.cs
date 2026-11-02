namespace pdf_fluent.Services;

public interface IPdfDocumentService
{
    (double Width, double Height)[] GetPageSizes(string filePath);

    (int width, int height, byte[] pixels) RenderPageToRawBuffer(
        string filePath,
        int pageNumber = 0,
        float scale = 1f);
}
