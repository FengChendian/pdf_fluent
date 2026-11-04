using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using pdf_fluent.Models;
using pdf_fluent.Services;

namespace pdf_fluent.ViewModels;

public sealed partial class MainViewModel : ViewModelBase, IDisposable
{
    private const int MaxRecentDocuments = 12;

    private readonly PDFiumService _pdfService;
    private readonly RecentDocumentsService _recentService;
    private readonly FavoriteDocumentsService _favoriteService;
    private bool _disposed;

    public MainViewModel() : this(
        new PDFiumService(),
        new RecentDocumentsService(),
        new FavoriteDocumentsService())
    {
    }

    internal MainViewModel(
        PDFiumService pdfService,
        RecentDocumentsService recentService,
        FavoriteDocumentsService favoriteService)
    {
        _pdfService = pdfService;
        _recentService = recentService;
        _favoriteService = favoriteService;

        foreach (var document in _recentService.Load().OrderByDescending(d => d.LastOpened).Take(MaxRecentDocuments))
            RecentDocuments.Add(new RecentDocumentViewModel(document, RemoveRecentDocumentCommand));

        foreach (var document in _favoriteService.Load().OrderByDescending(d => d.LastOpened))
            FavoriteDocuments.Add(new RecentDocumentViewModel(document));

        RefreshVisibleRecentDocuments();
    }

    public Func<Task<string?>>? PdfFilePicker { get; set; }
    public Func<DocumentTabViewModel, Task<DocumentCloseDecision>>? DocumentCloseConfirmation { get; set; }
    public ObservableCollection<DocumentTabViewModel> OpenDocuments { get; } = [];
    public ObservableCollection<RecentDocumentViewModel> RecentDocuments { get; } = [];
    public ObservableCollection<RecentDocumentViewModel> VisibleRecentDocuments { get; } = [];
    public ObservableCollection<RecentDocumentViewModel> FavoriteDocuments { get; } = [];
    public bool IsLibraryOpen => SelectedDocument is null;
    public bool IsHomeSelected => IsLibraryOpen && !IsFavoritesSelected;
    public bool IsDocumentOpen => SelectedDocument is not null;
    public bool HasRecentDocuments => RecentDocuments.Count > 0;
    public bool HasNoRecentDocuments => RecentDocuments.Count == 0;
    public bool HasNoVisibleRecentDocuments => VisibleRecentDocuments.Count == 0;
    public bool HasNoFavoriteDocuments => FavoriteDocuments.Count == 0;
    public bool HasFavoriteNotification => FavoriteDocuments.Any(document => document.IsUnread);
    public string RecentEmptyMessage => string.IsNullOrWhiteSpace(SearchText)
        ? "还没有最近阅读记录，打开一个 PDF 开始吧。"
        : "没有找到匹配的文档，换个关键词试试。";

    [ObservableProperty]
    private DocumentTabViewModel? _selectedDocument;

    [ObservableProperty]
    private bool _isFavoritesSelected;

    [ObservableProperty]
    private bool _isOpening;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string _searchText = string.Empty;

    partial void OnSelectedDocumentChanged(DocumentTabViewModel? oldValue, DocumentTabViewModel? newValue)
    {
        if (oldValue is not null) SaveReadingPosition(oldValue);
        if (newValue is not null) IsFavoritesSelected = false;
        foreach (var document in OpenDocuments)
            document.IsSelected = ReferenceEquals(document, newValue);
        OnPropertyChanged(nameof(IsLibraryOpen));
        OnPropertyChanged(nameof(IsHomeSelected));
        OnPropertyChanged(nameof(IsDocumentOpen));
    }

    partial void OnIsFavoritesSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsHomeSelected));
        OnPropertyChanged(nameof(HasFavoriteNotification));
    }

    partial void OnSearchTextChanged(string value)
    {
        RefreshVisibleRecentDocuments();
        OnPropertyChanged(nameof(RecentEmptyMessage));
    }

    [RelayCommand]
    private void ShowHome()
    {
        IsFavoritesSelected = false;
        SelectedDocument = null;
    }

    [RelayCommand]
    private void ShowFavorites()
    {
        SelectedDocument = null;
        IsFavoritesSelected = true;
        MarkFavoritesAsRead();
    }

    private void MarkFavoritesAsRead()
    {
        if (!FavoriteDocuments.Any(document => document.IsUnread)) return;

        foreach (var document in FavoriteDocuments) document.MarkAsRead();
        _favoriteService.Save(FavoriteDocuments.Select(document => document.ToModel()));
        OnPropertyChanged(nameof(HasFavoriteNotification));
    }

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
        RecentDocumentViewModel? recent = RecentDocuments.FirstOrDefault(d =>
            string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        var document = new DocumentTabViewModel(filePath, _pdfService, recent?.ScrollOffset)
        {
            IsFavorite = FavoriteDocuments.Any(d =>
                string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase)),
        };
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
    private void ToggleFavorite(DocumentTabViewModel? document)
    {
        if (document is null) return;

        StatusMessage = null;
        var existing = FavoriteDocuments.FirstOrDefault(d =>
            string.Equals(d.FilePath, document.FilePath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            FavoriteDocuments.Remove(existing);
            existing.Dispose();
            document.IsFavorite = false;
        }
        else
        {
            FavoriteDocuments.Insert(0, new RecentDocumentViewModel(new RecentDocument
            {
                FilePath = document.FilePath,
                PageCount = document.PageCount,
                LastOpened = DateTimeOffset.Now,
                IsUnread = true,
            }));
            document.IsFavorite = true;
        }

        _favoriteService.Save(FavoriteDocuments.Select(d => d.ToModel()));
        OnPropertyChanged(nameof(HasFavoriteNotification));
        OnPropertyChanged(nameof(HasNoFavoriteDocuments));
    }

    [RelayCommand]
    private async Task CloseDocumentAsync(DocumentTabViewModel? document)
    {
        if (document is null || !OpenDocuments.Contains(document)) return;
        if (!await ConfirmDocumentCloseAsync(document)) return;
        CloseDocumentCore(document);
    }

    [RelayCommand]
    private async Task CloseAllDocumentsAsync()
    {
        if (!await ConfirmCloseAllDocumentsAsync()) return;
        DocumentTabViewModel[] documents = OpenDocuments.ToArray();
        SelectedDocument = null;
        foreach (DocumentTabViewModel document in documents) SaveReadingPosition(document);
        OpenDocuments.Clear();
        foreach (DocumentTabViewModel document in documents) document.Dispose();
    }

    public async Task<bool> ConfirmCloseAllDocumentsAsync()
    {
        foreach (DocumentTabViewModel document in OpenDocuments.ToArray())
        {
            if (!await ConfirmDocumentCloseAsync(document)) return false;
        }
        return true;
    }

    private async Task<bool> ConfirmDocumentCloseAsync(DocumentTabViewModel document)
    {
        if (!document.HasUnsavedHighlights) return true;
        if (DocumentCloseConfirmation is null) return false;

        DocumentCloseDecision decision = await DocumentCloseConfirmation(document);
        switch (decision)
        {
            case DocumentCloseDecision.Discard:
                return true;
            case DocumentCloseDecision.Save:
                bool saved = await document.SaveHighlightsAsync();
                if (!saved) StatusMessage = document.HighlightErrorMessage;
                return saved;
            default:
                return false;
        }
    }

    private void CloseDocumentCore(DocumentTabViewModel document)
    {
        int index = OpenDocuments.IndexOf(document);
        if (ReferenceEquals(SelectedDocument, document))
        {
            SelectedDocument = OpenDocuments.Count > 1
                ? OpenDocuments[index == OpenDocuments.Count - 1 ? index - 1 : index + 1]
                : null;
        }

        SaveReadingPosition(document);
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
        RefreshVisibleRecentDocuments();
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
        RefreshVisibleRecentDocuments();
        OnPropertyChanged(nameof(HasRecentDocuments));
        OnPropertyChanged(nameof(HasNoRecentDocuments));
        StatusMessage = "最近记录已清除";
    }

    private void RefreshVisibleRecentDocuments()
    {
        string query = SearchText.Trim();
        IEnumerable<RecentDocumentViewModel> visible = string.IsNullOrEmpty(query)
            ? RecentDocuments
            : RecentDocuments.Where(document =>
                document.FileName.Contains(query, StringComparison.OrdinalIgnoreCase));

        VisibleRecentDocuments.Clear();
        foreach (var document in visible) VisibleRecentDocuments.Add(document);
        OnPropertyChanged(nameof(HasNoVisibleRecentDocuments));
    }

    private async Task AddRecentDocumentAsync(DocumentTabViewModel document)
    {
        byte[]? thumbnail = await CreateRecentThumbnailAsync(document.FilePath);
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
            ScrollOffsetX = document.ScrollOffset.X,
            ScrollOffsetY = document.ScrollOffset.Y,
            Thumbnail = thumbnail,
        }, RemoveRecentDocumentCommand));

        while (RecentDocuments.Count > MaxRecentDocuments)
        {
            var removed = RecentDocuments[^1];
            RecentDocuments.RemoveAt(RecentDocuments.Count - 1);
            removed.Dispose();
        }

        _recentService.Save(RecentDocuments.Select(d => d.ToModel()));
        RefreshVisibleRecentDocuments();
        OnPropertyChanged(nameof(HasRecentDocuments));
        OnPropertyChanged(nameof(HasNoRecentDocuments));
    }

    private void SaveReadingPosition(DocumentTabViewModel document)
    {
        RecentDocumentViewModel? recent = RecentDocuments.FirstOrDefault(d =>
            string.Equals(d.FilePath, document.FilePath, StringComparison.OrdinalIgnoreCase));
        if (recent is null) return;

        recent.UpdateScrollOffset(document.ScrollOffset);
        _recentService.Save(RecentDocuments.Select(d => d.ToModel()));
    }

    private async Task<byte[]?> CreateRecentThumbnailAsync(string filePath)
    {
        try
        {
            using WriteableBitmap bitmap = await Task.Run(() =>
                _pdfService.RenderPageToWriteableBitmap(filePath, 0, 0.28f));
            using var stream = new MemoryStream();
            bitmap.Save(stream, PngBitmapEncoderOptions.Default);
            return stream.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var document in OpenDocuments) SaveReadingPosition(document);
        foreach (var document in OpenDocuments) document.Dispose();
        foreach (var document in RecentDocuments) document.Dispose();
        foreach (var document in FavoriteDocuments) document.Dispose();
        OpenDocuments.Clear();
        RecentDocuments.Clear();
        VisibleRecentDocuments.Clear();
        FavoriteDocuments.Clear();
        _pdfService.Dispose();
    }
}
