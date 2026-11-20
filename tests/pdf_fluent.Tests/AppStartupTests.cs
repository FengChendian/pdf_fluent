using Avalonia.Headless.XUnit;
using pdf_fluent.Services;
using pdf_fluent.ViewModels;
using Xunit;

namespace pdf_fluent.Tests;

public sealed class AppStartupTests
{
    [AvaloniaFact]
    public async Task StartupAndSubsequentArgumentsOpenTabsInTheSameViewModel()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pdf_fluent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string pdfPath = Path.Combine(directory, "double click document.PDF");
        string secondPdfPath = Path.Combine(directory, "second document.pdf");
        string textPath = Path.Combine(directory, "notes.txt");
        File.Copy(PdfTestHelpers.SamplePdfPath(), pdfPath);
        File.Copy(PdfTestHelpers.SamplePdfPath(), secondPdfPath);
        File.WriteAllText(textPath, "Not a PDF");
        try
        {
            using var viewModel = new MainViewModel(
                new PDFiumService(),
                new RecentDocumentsService(Path.Combine(directory, "recent.json")),
                new FavoriteDocumentsService(Path.Combine(directory, "favorites.json")));

            await App.OpenStartupDocumentsAsync(viewModel, [pdfPath, textPath]);

            var document = Assert.Single(viewModel.OpenDocuments);
            Assert.Equal(pdfPath, document.FilePath);
            Assert.Same(document, viewModel.SelectedDocument);
            Assert.Null(document.ErrorMessage);
            Assert.True(document.IsLoaded);
            Assert.True(document.PageCount > 0);

            await App.OpenStartupDocumentsAsync(viewModel, [secondPdfPath]);

            Assert.Equal(2, viewModel.OpenDocuments.Count);
            Assert.Equal(new[] { pdfPath, secondPdfPath }, viewModel.OpenDocuments.Select(item => item.FilePath));
            Assert.Equal(secondPdfPath, viewModel.SelectedDocument?.FilePath);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
