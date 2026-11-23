using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using pdf_fluent.Diagnostics;
using pdf_fluent.Models;
using pdf_fluent.Services;

namespace pdf_fluent.ViewModels;

public sealed partial class MainViewModel : ViewModelBase, IDisposable
{
    private const int MaxRecentDocuments = 12;
    private static readonly TimeSpan StatusMessageDuration = TimeSpan.FromSeconds(3);

    private readonly PDFiumService _pdfService;
    private readonly RecentDocumentsService _recentService;
    private readonly FavoriteDocumentsService _favoriteService;
    private readonly DispatcherTimer _statusMessageTimer;
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
        FavoriteDocumentsService favoriteService,
        TimeSpan? statusMessageDuration = null,
        IUpdateCheckService? updateCheckService = null)
    {
        _pdfService = pdfService;
        _recentService = recentService;
        _favoriteService = favoriteService;
        StartupTrace.Mark("MainViewModel.ctor.enter");
        Settings = updateCheckService is null
            ? new SettingsViewModel()
            : new SettingsViewModel(updateCheckService, ApplicationVersion.Current);
        StartupTrace.Mark("MainViewModel.SettingsViewModel");
        _statusMessageTimer = new DispatcherTimer
        {
            Interval = statusMessageDuration ?? StatusMessageDuration,
        };
        _statusMessageTimer.Tick += OnStatusMessageTimerTick;
        StartupTrace.Mark("MainViewModel.DispatcherTimer");

        foreach (var document in _recentService.Load().OrderByDescending(d => d.LastOpened).Take(MaxRecentDocuments))
            RecentDocuments.Add(new RecentDocumentViewModel(document, RemoveRecentDocumentCommand));
        StartupTrace.Mark("MainViewModel.RecentDocuments.LoadAndDecodeThumbnails");

        foreach (var document in _favoriteService.Load().OrderByDescending(d => d.LastOpened))
        {
            var recent = RecentDocuments.FirstOrDefault(d =>
                string.Equals(d.FilePath, document.FilePath, StringComparison.OrdinalIgnoreCase));
            document.Thumbnail ??= recent?.ToModel().Thumbnail;
            FavoriteDocuments.Add(new RecentDocumentViewModel(
                document, RemoveFavoriteDocumentCommand, "从收藏夹移除"));
        }
        StartupTrace.Mark("MainViewModel.FavoriteDocuments.LoadAndDecodeThumbnails");

        RefreshVisibleRecentDocuments();
        StartupTrace.Mark("MainViewModel.ctor.exit");
    }

    public Func<Task<string?>>? PdfFilePicker { get; set; }
    public Func<DocumentTabViewModel, Task<DocumentCloseDecision>>? DocumentCloseConfirmation { get; set; }
    public ObservableCollection<DocumentTabViewModel> OpenDocuments { get; } = [];
    public ObservableCollection<RecentDocumentViewModel> RecentDocuments { get; } = [];
    public ObservableCollection<RecentDocumentViewModel> VisibleRecentDocuments { get; } = [];
    public ObservableCollection<RecentDocumentViewModel> FavoriteDocuments { get; } = [];
    public SettingsViewModel Settings { get; }
    public bool IsLibraryOpen => SelectedDocument is null && !IsSettingsSelected;
    public bool IsHomeSelected => IsLibraryOpen && !IsFavoritesSelected;
    public bool IsSettingsOpen => SelectedDocument is null && IsSettingsSelected;
    public bool IsDocumentOpen => SelectedDocument is not null;
    /// <summary>The single fixed workspace tab is active whenever no document tab is selected.</summary>
    public bool IsWorkspaceTabSelected => SelectedDocument is null;
    public string WorkspaceTabTitle => IsSettingsSelected ? "设置" : IsFavoritesSelected ? "收藏" : "主页";
    public string WorkspaceTabIcon => IsSettingsSelected
        ? "fa-solid fa-gear"
        : IsFavoritesSelected
            ? "fa-solid fa-star"
            : "fa-solid fa-house";
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
    private bool _isSettingsSelected;

    [ObservableProperty]
    private bool _isOpening;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string _searchText = string.Empty;

    partial void OnSelectedDocumentChanged(DocumentTabViewModel? oldValue, DocumentTabViewModel? newValue)
    {
        if (oldValue is not null) SaveReadingPosition(oldValue);
        if (newValue is not null)
        {
            IsFavoritesSelected = false;
            IsSettingsSelected = false;
        }
        foreach (var document in OpenDocuments)
            document.IsSelected = ReferenceEquals(document, newValue);
        OnPropertyChanged(nameof(IsLibraryOpen));
        OnPropertyChanged(nameof(IsHomeSelected));
        OnPropertyChanged(nameof(IsDocumentOpen));
        OnPropertyChanged(nameof(IsSettingsOpen));
        OnPropertyChanged(nameof(IsWorkspaceTabSelected));
    }

    partial void OnIsSettingsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsLibraryOpen));
        OnPropertyChanged(nameof(IsHomeSelected));
        OnPropertyChanged(nameof(IsSettingsOpen));
        OnPropertyChanged(nameof(WorkspaceTabTitle));
        OnPropertyChanged(nameof(WorkspaceTabIcon));
    }

    partial void OnIsFavoritesSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsHomeSelected));
        OnPropertyChanged(nameof(HasFavoriteNotification));
        OnPropertyChanged(nameof(WorkspaceTabTitle));
        OnPropertyChanged(nameof(WorkspaceTabIcon));
    }

    partial void OnSearchTextChanged(string value)
    {
        RefreshVisibleRecentDocuments();
        OnPropertyChanged(nameof(RecentEmptyMessage));
    }

    partial void OnStatusMessageChanged(string? value)
    {
        _statusMessageTimer.Stop();
        if (value is not null) _statusMessageTimer.Start();
    }

    private void OnStatusMessageTimerTick(object? sender, EventArgs e) => StatusMessage = null;

    [RelayCommand]
    private void ShowHome()
    {
        IsFavoritesSelected = false;
        IsSettingsSelected = false;
        SelectedDocument = null;
        // The fixed tab and the sidebar button are ToggleButtons whose IsChecked is
        // bound one-way. Re-raise the computed flags so clicking the already active
        // workspace control cannot leave it visually unchecked.
        OnPropertyChanged(nameof(IsHomeSelected));
        OnPropertyChanged(nameof(IsWorkspaceTabSelected));
    }

    [RelayCommand]
    private void ShowFavorites()
    {
        IsSettingsSelected = false;
        SelectedDocument = null;
        IsFavoritesSelected = true;
        OnPropertyChanged(nameof(IsFavoritesSelected));
        MarkFavoritesAsRead();
    }

    [RelayCommand]
    private void ShowSettings()
    {
        IsFavoritesSelected = false;
        SelectedDocument = null;
        IsSettingsSelected = true;
        OnPropertyChanged(nameof(IsSettingsSelected));
    }

    public Task CheckForUpdatesOnStartupAsync() => Settings.CheckForUpdatesOnStartupAsync();

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

        StartupTrace.Mark($"OpenDocumentAsync.enter ({Path.GetFileName(filePath)})");
        var existing = OpenDocuments.FirstOrDefault(d =>
            string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SelectedDocument = existing;
            StartupTrace.Mark("OpenDocumentAsync.existing");
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
        StartupTrace.Mark("OpenDocumentAsync.DocumentCreated");

        try
        {
            await document.LoadAsync();
            StartupTrace.Mark("OpenDocumentAsync.LoadAsync.done");
            if (document.ErrorMessage is null && OpenDocuments.Contains(document))
                await AddRecentDocumentAsync(document);
            StartupTrace.Mark("OpenDocumentAsync.AddRecentDocument.done");
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
            var recent = RecentDocuments.FirstOrDefault(d =>
                string.Equals(d.FilePath, document.FilePath, StringComparison.OrdinalIgnoreCase));
            FavoriteDocuments.Insert(0, new RecentDocumentViewModel(new RecentDocument
            {
                FilePath = document.FilePath,
                PageCount = document.PageCount,
                LastOpened = DateTimeOffset.Now,
                IsUnread = true,
                Thumbnail = recent?.ToModel().Thumbnail,
            }, RemoveFavoriteDocumentCommand, "从收藏夹移除"));
            document.IsFavorite = true;
        }

        _favoriteService.Save(FavoriteDocuments.Select(d => d.ToModel()));
        OnPropertyChanged(nameof(HasFavoriteNotification));
        OnPropertyChanged(nameof(HasNoFavoriteDocuments));
    }

    [RelayCommand]
    private void RemoveFavoriteDocument(RecentDocumentViewModel? document)
    {
        if (document is null || !FavoriteDocuments.Remove(document)) return;

        string fileName = document.FileName;
        foreach (var openDocument in OpenDocuments.Where(d =>
                     string.Equals(d.FilePath, document.FilePath, StringComparison.OrdinalIgnoreCase)))
            openDocument.IsFavorite = false;
        document.Dispose();
        _favoriteService.Save(FavoriteDocuments.Select(d => d.ToModel()));
        OnPropertyChanged(nameof(HasFavoriteNotification));
        OnPropertyChanged(nameof(HasNoFavoriteDocuments));
        StatusMessage = $"已从收藏夹移除：{fileName}";
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
            ScrollLayoutVersion = 1,
            Thumbnail = thumbnail,
        }, RemoveRecentDocumentCommand));

        while (RecentDocuments.Count > MaxRecentDocuments)
        {
            var removed = RecentDocuments[^1];
            RecentDocuments.RemoveAt(RecentDocuments.Count - 1);
            removed.Dispose();
        }

        _recentService.Save(RecentDocuments.Select(d => d.ToModel()));
        var favorite = FavoriteDocuments.FirstOrDefault(d =>
            string.Equals(d.FilePath, document.FilePath, StringComparison.OrdinalIgnoreCase));
        if (favorite is not null && thumbnail is not null)
        {
            favorite.UpdateThumbnail(thumbnail);
            _favoriteService.Save(FavoriteDocuments.Select(d => d.ToModel()));
        }
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
        _statusMessageTimer.Stop();
        _statusMessageTimer.Tick -= OnStatusMessageTimerTick;
        foreach (var document in OpenDocuments) SaveReadingPosition(document);
        foreach (var document in OpenDocuments) document.Dispose();
        foreach (var document in RecentDocuments) document.Dispose();
        foreach (var document in FavoriteDocuments) document.Dispose();
        OpenDocuments.Clear();
        RecentDocuments.Clear();
        VisibleRecentDocuments.Clear();
        FavoriteDocuments.Clear();
        Settings.Dispose();
        _pdfService.Dispose();
    }
}
