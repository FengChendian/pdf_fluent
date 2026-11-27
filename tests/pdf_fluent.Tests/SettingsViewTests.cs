using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Optris.Icons.Avalonia;
using pdf_fluent.Services;
using pdf_fluent.ViewModels;
using pdf_fluent.Views;
using Xunit;

namespace pdf_fluent.Tests;

public sealed class SettingsViewTests
{
    [AvaloniaFact]
    public async Task UpdateRowUsesTheSettingsLayoutAndAnimatesWhileChecking()
    {
        var completion = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new StubUpdateService(completion.Task);
        var download = new TaskCompletionSource<IPreparedUpdate>();
        var installer = new UpdateTestData.Installer((_, _, _) => download.Task);
        using var viewModel = new MainViewModel(
            new PDFiumService(),
            new RecentDocumentsService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "recent.json")),
            new FavoriteDocumentsService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "favorites.json")),
            updateCheckService: service, updateInstallerService: installer);
        var window = new MainWindow { DataContext = viewModel };
        try
        {
            window.Show();
            viewModel.ShowSettingsCommand.Execute(null);
            window.UpdateLayout();

            Border card = window.GetVisualDescendants().OfType<Border>()
                .Single(border => border.Name == "UpdateSettingsCard");
            Border aboutCard = window.GetVisualDescendants().OfType<Border>()
                .Single(border => border.Name == "AboutSettingsCard");
            Button action = window.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Name == "UpdateActionButton");
            TextBlock title = window.GetVisualDescendants().OfType<TextBlock>()
                .Single(text => text.Text == "应用更新");
            TextBlock status = window.GetVisualDescendants().OfType<TextBlock>()
                .Single(text => text.Name == "UpdateStatusText");
            Assert.Equal(new CornerRadius(16), card.CornerRadius);
            Assert.Equal(104, card.Bounds.Height, precision: 1);
            Assert.Equal(card.Bounds.Height, aboutCard.Bounds.Height, precision: 1);
            Assert.True(action.TranslatePoint(new Point(), card)!.Value.X >
                        title.TranslatePoint(new Point(), card)!.Value.X);
            AssertHorizontallyCentered(status, action, card);
            Assert.Equal("检查更新", viewModel.Settings.UpdateButtonText);

            Task actionTask = Assert.IsAssignableFrom<IAsyncRelayCommand>(action.Command).ExecuteAsync(null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Icon spinner = window.GetVisualDescendants().OfType<Icon>()
                .Single(icon => icon.Name == "UpdateProgressRing");
            Assert.True(viewModel.Settings.IsCheckingUpdate);
            Assert.True(spinner.IsVisible);
            Assert.Contains("update-spinner", spinner.Classes);
            Assert.IsType<RotateTransform>(spinner.RenderTransform);
            Assert.False(action.Command!.CanExecute(null));

            completion.SetResult(UpdateTestData.Result("9.0.0"));
            await actionTask;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.False(spinner.IsVisible);
            Assert.True(action.IsEnabled);
            AssertHorizontallyCentered(status, action, card);
            Assert.Equal("下载并更新", viewModel.Settings.UpdateButtonText);
            Assert.Contains("available", action.Classes);
            Assert.Equal(Color.Parse("#6C5CE7"), Assert.IsAssignableFrom<ISolidColorBrush>(action.Background).Color);
            Point center = action.TranslatePoint(
                new Point(action.Bounds.Width / 2, action.Bounds.Height / 2), window)!.Value;
            window.MouseMove(center);
            ContentPresenter surface = action.GetVisualChildren().OfType<ContentPresenter>().Single();
            Assert.Equal(Color.Parse("#5E50D7"), Assert.IsAssignableFrom<ISolidColorBrush>(surface.Background).Color);

            viewModel.Settings.CloseForUpdateAsync = commit => { commit(); return Task.FromResult(true); };
            Task updating = Assert.IsAssignableFrom<IAsyncRelayCommand>(action.Command).ExecuteAsync(null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(spinner.IsVisible);
            Assert.False(action.IsEffectivelyEnabled);
            var prepared = new UpdateTestData.PreparedUpdate();
            download.SetResult(prepared);
            await updating;
            Assert.True(prepared.Committed);
        }
        finally
        {
            if (window.IsVisible) window.Close();
        }
    }

    [AvaloniaFact]
    public async Task UpdateShutdownHonorsUnsavedHighlightCancellationBeforeCommitting()
    {
        using var viewModel = new MainViewModel();
        using var pdfService = new FakePdfDocumentService(1);
        using var document = new DocumentTabViewModel("missing.pdf", pdfService);
        await document.LoadAsync();
        await document.AddWordHighlightAsync(0, 1);
        viewModel.OpenDocuments.Add(document);
        var window = new MainWindow { DataContext = viewModel };
        bool committed = false;
        try
        {
            window.Show();
            viewModel.DocumentCloseConfirmation = _ => Task.FromResult(DocumentCloseDecision.Cancel);
            Assert.False(await window.CloseForUpdateAsync(() => committed = true));
            Assert.False(committed);
            Assert.True(window.IsVisible);

            viewModel.DocumentCloseConfirmation = _ => Task.FromResult(DocumentCloseDecision.Discard);
            Assert.True(await window.CloseForUpdateAsync(() =>
            {
                Assert.True(window.IsVisible);
                committed = true;
            }));
            Assert.True(committed);
            Assert.False(window.IsVisible);
        }
        finally
        {
            viewModel.DocumentCloseConfirmation = _ => Task.FromResult(DocumentCloseDecision.Discard);
            if (window.IsVisible) window.Close();
        }
    }

    private static void AssertHorizontallyCentered(Control status, Control action, Border relativeTo)
    {
        double statusCenter = status.TranslatePoint(
            new Point(status.Bounds.Width / 2, 0), relativeTo)!.Value.X;
        double actionCenter = action.TranslatePoint(
            new Point(action.Bounds.Width / 2, 0), relativeTo)!.Value.X;
        Assert.Equal(actionCenter, statusCenter, precision: 1);
    }

    private sealed class StubUpdateService(Task<UpdateCheckResult> result) : IUpdateCheckService
    {
        public Task<UpdateCheckResult> CheckForUpdateAsync(
            Version currentVersion,
            CancellationToken cancellationToken = default) => result;
    }
}
