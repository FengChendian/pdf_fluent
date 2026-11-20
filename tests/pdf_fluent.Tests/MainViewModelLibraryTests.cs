using Avalonia.Headless.XUnit;
using pdf_fluent.Models;
using pdf_fluent.Services;
using pdf_fluent.ViewModels;
using Xunit;

namespace pdf_fluent.Tests;

public sealed class MainViewModelLibraryTests
{
    [AvaloniaFact]
    public void SearchFiltersRecentDocumentsWithoutChangingTheStoredCollection()
    {
        using var viewModel = new MainViewModel();
        viewModel.RecentDocuments.Clear();
        viewModel.RecentDocuments.Add(new RecentDocumentViewModel(new RecentDocument
        {
            FilePath = "alpha-guide.pdf",
            PageCount = 10,
            LastOpened = DateTimeOffset.Now,
        }));
        viewModel.RecentDocuments.Add(new RecentDocumentViewModel(new RecentDocument
        {
            FilePath = "beta-notes.pdf",
            PageCount = 4,
            LastOpened = DateTimeOffset.Now,
        }));
        viewModel.SearchText = "alpha";

        Assert.Equal(2, viewModel.RecentDocuments.Count);
        Assert.Single(viewModel.VisibleRecentDocuments);
        Assert.Equal("alpha-guide", viewModel.VisibleRecentDocuments[0].FileName);
        Assert.False(viewModel.HasNoVisibleRecentDocuments);

        viewModel.SearchText = "missing";
        Assert.Empty(viewModel.VisibleRecentDocuments);
        Assert.True(viewModel.HasNoVisibleRecentDocuments);
        Assert.Equal("没有找到匹配的文档，换个关键词试试。", viewModel.RecentEmptyMessage);
    }

    [AvaloniaFact]
    public void SelectingADocumentTabLeavesTheFavoritesPage()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pdf_fluent-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var viewModel = new MainViewModel(
                new PDFiumService(),
                new RecentDocumentsService(Path.Combine(directory, "recent.json")),
                new FavoriteDocumentsService(Path.Combine(directory, "favorites.json")));
            using var service = new FakePdfDocumentService(3);
            using var document = new DocumentTabViewModel("document.pdf", service);
            viewModel.OpenDocuments.Add(document);
            viewModel.ShowFavoritesCommand.Execute(null);
            Assert.True(viewModel.IsFavoritesSelected);

            viewModel.SelectDocumentCommand.Execute(document);

            Assert.False(viewModel.IsFavoritesSelected);
            Assert.True(viewModel.IsDocumentOpen);
            Assert.False(viewModel.IsLibraryOpen);
            Assert.False(viewModel.IsHomeSelected);
            Assert.True(document.IsSelected);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task FavoriteReadStatePersistsAfterVisitingTheFavoritesPage()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pdf_fluent-tests", Guid.NewGuid().ToString("N"));
        string storagePath = Path.Combine(directory, "favorites.json");
        string recentPath = Path.Combine(directory, "recent.json");
        try
        {
            using var viewModel = new MainViewModel(
                new PDFiumService(),
                new RecentDocumentsService(recentPath),
                new FavoriteDocumentsService(storagePath));
            using var service = new FakePdfDocumentService(5);
            using var document = new DocumentTabViewModel("favorite.pdf", service);
            await document.LoadAsync();

            Assert.False(viewModel.HasFavoriteNotification);
            viewModel.ToggleFavoriteCommand.Execute(document);

            Assert.True(document.IsFavorite);
            Assert.Null(viewModel.StatusMessage);
            Assert.Single(viewModel.FavoriteDocuments);
            Assert.True(viewModel.HasFavoriteNotification);
            Assert.Equal("取消收藏", document.FavoriteToolTip);
            Assert.True(new FavoriteDocumentsService(storagePath).Load().Single().IsUnread);

            using (var restoredUnread = new MainViewModel(
                       new PDFiumService(),
                       new RecentDocumentsService(recentPath),
                       new FavoriteDocumentsService(storagePath)))
            {
                Assert.True(restoredUnread.HasFavoriteNotification);
            }

            viewModel.ShowFavoritesCommand.Execute(null);
            Assert.False(viewModel.HasFavoriteNotification);
            viewModel.ShowHomeCommand.Execute(null);
            Assert.False(viewModel.HasFavoriteNotification);

            var saved = new FavoriteDocumentsService(storagePath).Load();
            Assert.Single(saved);
            Assert.Equal("favorite.pdf", saved[0].FilePath);
            Assert.Equal(5, saved[0].PageCount);
            Assert.False(saved[0].IsUnread);

            using var restoredRead = new MainViewModel(
                new PDFiumService(),
                new RecentDocumentsService(recentPath),
                new FavoriteDocumentsService(storagePath));
            Assert.False(restoredRead.HasFavoriteNotification);

            viewModel.ToggleFavoriteCommand.Execute(document);
            Assert.False(document.IsFavorite);
            Assert.Empty(viewModel.FavoriteDocuments);
            Assert.False(viewModel.HasFavoriteNotification);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public void FavoriteDocumentsReuseRecentThumbnailsAndRemoveOnlyTheFavorite()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pdf_fluent-tests", Guid.NewGuid().ToString("N"));
        string recentPath = Path.Combine(directory, "recent.json");
        string favoritesPath = Path.Combine(directory, "favorites.json");
        byte[] thumbnail = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        try
        {
            new RecentDocumentsService(recentPath).Save([
                new RecentDocument
                {
                    FilePath = "document.pdf",
                    PageCount = 8,
                    LastOpened = DateTimeOffset.Now,
                    Thumbnail = thumbnail,
                },
            ]);
            new FavoriteDocumentsService(favoritesPath).Save([
                new RecentDocument
                {
                    FilePath = "document.pdf",
                    PageCount = 8,
                    LastOpened = DateTimeOffset.Now,
                },
            ]);
            using var viewModel = new MainViewModel(
                new PDFiumService(),
                new RecentDocumentsService(recentPath),
                new FavoriteDocumentsService(favoritesPath));
            using var service = new FakePdfDocumentService(8);
            using var document = new DocumentTabViewModel("document.pdf", service) { IsFavorite = true };
            viewModel.OpenDocuments.Add(document);

            var favorite = Assert.Single(viewModel.FavoriteDocuments);
            Assert.NotNull(favorite.Thumbnail);
            Assert.Equal(thumbnail, favorite.ToModel().Thumbnail);
            Assert.Equal("从收藏夹移除", favorite.RemoveActionText);

            favorite.RemoveCommand!.Execute(favorite);

            Assert.Empty(viewModel.FavoriteDocuments);
            Assert.Single(viewModel.RecentDocuments);
            Assert.False(document.IsFavorite);
            Assert.Empty(new FavoriteDocumentsService(favoritesPath).Load());
            Assert.Single(new RecentDocumentsService(recentPath).Load());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public void AddingAFavoriteCopiesTheCachedRecentThumbnail()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pdf_fluent-tests", Guid.NewGuid().ToString("N"));
        string recentPath = Path.Combine(directory, "recent.json");
        string favoritesPath = Path.Combine(directory, "favorites.json");
        byte[] thumbnail = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        try
        {
            new RecentDocumentsService(recentPath).Save([
                new RecentDocument
                {
                    FilePath = "document.pdf",
                    PageCount = 8,
                    LastOpened = DateTimeOffset.Now,
                    Thumbnail = thumbnail,
                },
            ]);
            using var viewModel = new MainViewModel(
                new PDFiumService(),
                new RecentDocumentsService(recentPath),
                new FavoriteDocumentsService(favoritesPath));
            using var service = new FakePdfDocumentService(8);
            using var document = new DocumentTabViewModel("document.pdf", service);
            viewModel.OpenDocuments.Add(document);

            viewModel.ToggleFavoriteCommand.Execute(document);

            var favorite = Assert.Single(viewModel.FavoriteDocuments);
            Assert.NotNull(favorite.Thumbnail);
            Assert.Equal(thumbnail, favorite.ToModel().Thumbnail);
            Assert.Equal(thumbnail, new FavoriteDocumentsService(favoritesPath).Load().Single().Thumbnail);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public void RecentDocumentThumbnailsSurviveSavingAndReloading()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pdf_fluent-tests", Guid.NewGuid().ToString("N"));
        string recentPath = Path.Combine(directory, "recent.json");
        string favoritesPath = Path.Combine(directory, "favorites.json");
        try
        {
            byte[] thumbnail = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

            new RecentDocumentsService(recentPath).Save([
                new RecentDocument
                {
                    FilePath = "document.pdf",
                    PageCount = 8,
                    LastOpened = DateTimeOffset.Now,
                    Thumbnail = thumbnail,
                },
            ]);
            var stored = Assert.Single(new RecentDocumentsService(recentPath).Load());
            Assert.NotEmpty(thumbnail);
            Assert.Equal(thumbnail, stored.Thumbnail);
            using var direct = new RecentDocumentViewModel(stored);
            Assert.Equal(thumbnail, direct.ToModel().Thumbnail);
            Assert.NotNull(direct.Thumbnail);

            using var viewModel = new MainViewModel(
                new PDFiumService(),
                new RecentDocumentsService(recentPath),
                new FavoriteDocumentsService(favoritesPath));
            var document = Assert.Single(viewModel.RecentDocuments);
            var restored = Assert.IsAssignableFrom<Avalonia.Media.Imaging.Bitmap>(document.Thumbnail);
            Assert.Equal(new Avalonia.PixelSize(1, 1), restored.PixelSize);
            Assert.Equal(thumbnail, document.ToModel().Thumbnail);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task FavoriteRemovalStatusMessageDisappearsAutomatically()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pdf_fluent-tests", Guid.NewGuid().ToString("N"));
        string recentPath = Path.Combine(directory, "recent.json");
        string favoritesPath = Path.Combine(directory, "favorites.json");
        try
        {
            new FavoriteDocumentsService(favoritesPath).Save([
                new RecentDocument
                {
                    FilePath = "favorite.pdf",
                    PageCount = 3,
                    LastOpened = DateTimeOffset.Now,
                },
            ]);
            using var viewModel = new MainViewModel(
                new PDFiumService(),
                new RecentDocumentsService(recentPath),
                new FavoriteDocumentsService(favoritesPath),
                TimeSpan.FromMilliseconds(30));
            var favorite = Assert.Single(viewModel.FavoriteDocuments);

            viewModel.RemoveFavoriteDocumentCommand.Execute(favorite);

            Assert.Equal("已从收藏夹移除：favorite", viewModel.StatusMessage);
            await Task.Delay(150);
            Assert.Null(viewModel.StatusMessage);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
