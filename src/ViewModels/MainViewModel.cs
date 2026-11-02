using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using pdf_fluent.Models;
using pdf_fluent.Services;

namespace pdf_fluent.ViewModels;

public sealed partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly PDFiumService _pdfService = new();
    private readonly RecentDocumentsService _recentService = new();
    private bool _disposed;

    public MainViewModel()
    {
        foreach (var document in _recentService.Load().OrderByDescending(d => d.LastOpened).Take(12))
            RecentDocuments.Add(new RecentDocumentViewModel(document, RemoveRecentDocumentCommand));
    }

    public Func<Task<string?>>? PdfFilePicker { get; set; }
    public ObservableCollection<DocumentTabViewModel> OpenDocuments { get; } = [];
    public ObservableCollection<RecentDocumentViewModel> RecentDocuments { get; } = [];
    public bool IsHomeSelected => SelectedDocument is null;
    public bool IsDocumentOpen => SelectedDocument is not null;
    public bool HasRecentDocuments => RecentDocuments.Count > 0;
    public bool HasNoRecentDocuments => RecentDocuments.Count == 0;

    [ObservableProperty]
    private DocumentTabViewModel? _selectedDocument;

    [ObservableProperty]
    private bool _isOpening;

    [ObservableProperty]
    private string? _statusMessage;

    partial void OnSelectedDocumentChanged(DocumentTabViewModel? value)
    {
        foreach (var document in OpenDocuments)
            document.IsSelected = ReferenceEquals(document, value);
        OnPropertyChanged(nameof(IsHomeSelected));
        OnPropertyChanged(nameof(IsDocumentOpen));
    }

    [RelayCommand]
    private void ShowHome() => SelectedDocument = null;

    [RelayCommand]
    private void SelectDocument(DocumentTabViewModel? document) => SelectedDocument = document;

    [RelayCommand]
    private async Task OpenPdfAsync()
    {
        if (IsOpening || PdfFilePicker is null) return;
        var filePath = await PdfFilePicker();
        if (!string.IsNullOrWhiteSpace(filePath))
            await OpenDocumentAsync(filePath);
    }

    public async Task OpenDocumentAsync(string filePath)
    {
        if (IsOpening || _disposed) return;

        var existing = OpenDocuments.FirstOrDefault(d =>
            string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SelectedDocument = existing;
            return;
        }

        IsOpening = true;
        StatusMessage = null;
        var document = new DocumentTabViewModel(filePath, _pdfService);
        OpenDocuments.Add(document);
        SelectedDocument = document;

        try
        {
            await document.LoadAsync();
            if (document.ErrorMessage is null && OpenDocuments.Contains(document))
                await AddRecentDocumentAsync(document);
        }
        finally
        {
            IsOpening = false;
        }
    }

    [RelayCommand]
    private Task OpenRecentAsync(RecentDocumentViewModel? document) =>
        document is null ? Task.CompletedTask : OpenDocumentAsync(document.FilePath);

    [RelayCommand]
    private void CloseDocument(DocumentTabViewModel? document)
    {
        if (document is null) return;
        int index = OpenDocuments.IndexOf(document);
        if (index < 0) return;

        if (ReferenceEquals(SelectedDocument, document))
        {
            SelectedDocument = OpenDocuments.Count > 1
                ? OpenDocuments[index == OpenDocuments.Count - 1 ? index - 1 : index + 1]
                : null;
        }

        OpenDocuments.Remove(document);
        document.Dispose();
    }

    [RelayCommand]
    private void RemoveRecentDocument(RecentDocumentViewModel? document)
    {
        if (document is null || !RecentDocuments.Remove(document)) return;

        string fileName = document.FileName;
        document.Dispose();
        _recentService.Save(RecentDocuments.Select(d => d.ToModel()));
        OnPropertyChanged(nameof(HasRecentDocuments));
        OnPropertyChanged(nameof(HasNoRecentDocuments));
        StatusMessage = $"已删除最近记录：{fileName}";
    }

    [RelayCommand]
    private void ClearRecentDocuments()
    {
        foreach (var document in RecentDocuments) document.Dispose();
        RecentDocuments.Clear();
        _recentService.Save([]);
        OnPropertyChanged(nameof(HasRecentDocuments));
        OnPropertyChanged(nameof(HasNoRecentDocuments));
        StatusMessage = "最近记录已清除";
    }

    private async Task AddRecentDocumentAsync(DocumentTabViewModel document)
    {
        WriteableBitmap? thumbnail = null;
        try
        {
            var raw = await Task.Run(() => _pdfService.RenderPageToRawBuffer(document.FilePath, 0, 0.28f));
            thumbnail = PDFiumService.CreateWriteableBitmapFromRawBuffer(raw.width, raw.height, raw.pixels);
        }
        catch (Exception)
        {
        }

        var existing = RecentDocuments.FirstOrDefault(d =>
            string.Equals(d.FilePath, document.FilePath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            RecentDocuments.Remove(existing);
            existing.Dispose();
        }

        RecentDocuments.Insert(0, new RecentDocumentViewModel(new RecentDocument
        {
            FilePath = document.FilePath,
            PageCount = document.PageCount,
            LastOpened = DateTimeOffset.Now,
        }, RemoveRecentDocumentCommand)
        {
            Thumbnail = thumbnail,
        });

        while (RecentDocuments.Count > 12)
        {
            var removed = RecentDocuments[^1];
            RecentDocuments.RemoveAt(RecentDocuments.Count - 1);
            removed.Dispose();
        }

        _recentService.Save(RecentDocuments.Select(d => d.ToModel()));
        OnPropertyChanged(nameof(HasRecentDocuments));
        OnPropertyChanged(nameof(HasNoRecentDocuments));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var document in OpenDocuments) document.Dispose();
        foreach (var document in RecentDocuments) document.Dispose();
        OpenDocuments.Clear();
        RecentDocuments.Clear();
        _pdfService.Dispose();
    }
}
