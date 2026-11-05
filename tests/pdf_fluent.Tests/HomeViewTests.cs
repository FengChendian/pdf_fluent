using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using pdf_fluent.Models;
using pdf_fluent.ViewModels;
using pdf_fluent.Views;
using Xunit;

namespace pdf_fluent.Tests;

public sealed class HomeViewTests
{
    [AvaloniaFact]
    public void MinimumWidthKeepsSearchAndQuickActionsAtUsableSizes()
    {
        using var viewModel = new MainViewModel();
        var window = new MainWindow
        {
            Width = 1040,
            Height = 700,
            DataContext = viewModel,
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            TextBox search = window.GetVisualDescendants().OfType<TextBox>()
                .Single(textBox => textBox.Name == "DocumentSearchBox");
            Button[] quickActions = window.GetVisualDescendants().OfType<Button>()
                .Where(button => button.Classes.Contains("quick-card"))
                .ToArray();

            Assert.Equal(340, search.Bounds.Width, precision: 1);
            Assert.Equal(34, search.Bounds.Height, precision: 1);
            Assert.Equal(2, quickActions.Length);
            Assert.All(quickActions, button => Assert.True(button.Bounds.Width >= 280));
            Assert.Equal(quickActions[0].Bounds.Width, quickActions[1].Bounds.Width, precision: 1);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task RecentDocumentContextMenuOpensWithoutSelectingTheFirstItem()
    {
        using var viewModel = new MainViewModel();
        var recent = new RecentDocumentViewModel(new RecentDocument
        {
            FilePath = "document.pdf",
            PageCount = 1,
            LastOpened = DateTimeOffset.Now,
        });
        viewModel.RecentDocuments.Add(recent);
        viewModel.VisibleRecentDocuments.Add(recent);
        var window = new MainWindow
        {
            Width = 1040,
            Height = 700,
            DataContext = viewModel,
        };
        ContextMenu? menu = null;

        try
        {
            window.Show();
            window.UpdateLayout();
            Button card = window.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Classes.Contains("recent-card") &&
                                  ReferenceEquals(button.DataContext, recent));
            menu = card.ContextMenu;
            Assert.NotNull(menu);
            Point center = card.TranslatePoint(
                new Point(card.Bounds.Width / 2, card.Bounds.Height / 2), window)!.Value;

            window.MouseDown(center, MouseButton.Right);
            window.MouseUp(center, MouseButton.Right);
            window.UpdateLayout();

            Assert.True(menu.IsOpen);
            Assert.IsType<MenuItem>(menu.Items.Single());
            Assert.Equal(-1, menu.SelectedIndex);
        }
        finally
        {
            menu?.Close();
            recent.Dispose();
            window.Close();
        }
    }
}
