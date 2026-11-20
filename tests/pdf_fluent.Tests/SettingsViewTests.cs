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
        using var viewModel = new MainViewModel(
            new PDFiumService(),
            new RecentDocumentsService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "recent.json")),
            new FavoriteDocumentsService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "favorites.json")),
            updateCheckService: service);
        string? openedUrl = null;
        viewModel.Settings.ReleaseUrlOpener = url => openedUrl = url;
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

            completion.SetResult(new UpdateCheckResult(
                "v9.0.0", "https://github.com/FengChendian/pdf_fluent/releases/tag/v9.0.0", true));
            await actionTask;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.False(spinner.IsVisible);
            Assert.True(action.IsEnabled);
            AssertHorizontallyCentered(status, action, card);
            Assert.Equal("前往下载", viewModel.Settings.UpdateButtonText);
            Assert.Contains("available", action.Classes);
            Assert.Equal(Color.Parse("#6C5CE7"), Assert.IsAssignableFrom<ISolidColorBrush>(action.Background).Color);
            Point center = action.TranslatePoint(
                new Point(action.Bounds.Width / 2, action.Bounds.Height / 2), window)!.Value;
            window.MouseMove(center);
            ContentPresenter surface = action.GetVisualChildren().OfType<ContentPresenter>().Single();
            Assert.Equal(Color.Parse("#5E50D7"), Assert.IsAssignableFrom<ISolidColorBrush>(surface.Background).Color);

            await Assert.IsAssignableFrom<IAsyncRelayCommand>(action.Command).ExecuteAsync(null);
            Assert.Equal("https://github.com/FengChendian/pdf_fluent/releases/tag/v9.0.0", openedUrl);
        }
        finally
        {
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
