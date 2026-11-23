using Avalonia.Controls;
using Avalonia.Interactivity;
using pdf_fluent.Diagnostics;

namespace pdf_fluent.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        StartupTrace.Mark("HomeView.ctor.enter");
        InitializeComponent();
        StartupTrace.Mark("HomeView.ctor.exit");
    }

    private void OnRecentContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu) menu.SelectedIndex = -1;
    }

    private void OnRecentContextMenuGotFocus(object? sender, Avalonia.Input.FocusChangedEventArgs e)
    {
        if (sender is ContextMenu menu) menu.SelectedIndex = -1;
    }
}
