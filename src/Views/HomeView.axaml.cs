using Avalonia.Controls;
using Avalonia.Interactivity;

namespace pdf_fluent.Views;

public partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();

    private void OnRecentContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu) menu.SelectedIndex = -1;
    }

    private void OnRecentContextMenuGotFocus(object? sender, Avalonia.Input.FocusChangedEventArgs e)
    {
        if (sender is ContextMenu menu) menu.SelectedIndex = -1;
    }
}
