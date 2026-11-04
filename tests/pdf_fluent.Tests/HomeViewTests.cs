using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
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
}
