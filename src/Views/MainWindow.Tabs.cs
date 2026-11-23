using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using pdf_fluent.ViewModels;

namespace pdf_fluent.Views;

public partial class MainWindow
{
    public static readonly StyledProperty<double> DocumentTabWidthProperty =
        AvaloniaProperty.Register<MainWindow, double>(nameof(DocumentTabWidth), 224);
    public double DocumentTabWidth { get => GetValue(DocumentTabWidthProperty); set => SetValue(DocumentTabWidthProperty, value); }
    private MainViewModel? _tabViewModel;
    private bool _tabUpdateQueued;

    private void AttachTabViewModel()
    {
        DetachTabViewModel();
        _tabViewModel = DataContext as MainViewModel;
        if (_tabViewModel is null) return;
        _tabViewModel.PropertyChanged += OnTabSelectionChanged;
        _tabViewModel.OpenDocuments.CollectionChanged += OnTabsChanged;
        QueueTabUpdate();
    }

    private void DetachTabViewModel()
    {
        if (_tabViewModel is null) return;
        _tabViewModel.PropertyChanged -= OnTabSelectionChanged;
        _tabViewModel.OpenDocuments.CollectionChanged -= OnTabsChanged;
        _tabViewModel = null;
    }

    private void OnTabSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SelectedDocument) or nameof(MainViewModel.IsSettingsOpen) or nameof(MainViewModel.IsFavoritesSelected))
            QueueTabUpdate();
    }
    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueTabUpdate();
    private void OnTabStripSizeChanged(object? sender, SizeChangedEventArgs e) => QueueTabUpdate();
    private void OnTabContainerPrepared(object? sender, ContainerPreparedEventArgs e) => QueueTabUpdate();
    private void QueueTabUpdate()
    {
        if (_tabUpdateQueued) return;
        _tabUpdateQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _tabUpdateQueued = false;
            if (_tabViewModel is null) return;
            int count = _tabViewModel.OpenDocuments.Count;
            // Account for both arrow buttons before choosing whether to scroll.
            double available = DocumentTabScroller.Bounds.Width + (ScrollTabsLeftButton.IsVisible ? 56 : 0);
            bool overflow = count * 144 > available;
            ScrollTabsLeftButton.IsVisible = ScrollTabsRightButton.IsVisible = overflow;
            DocumentTabWidth = count == 0 ? 224 : Math.Clamp((available - (overflow ? 56 : 0)) / count, 144, 224);
            DocumentTabs.UpdateLayout();
            var selected = DocumentTabs.GetVisualDescendants().OfType<ToggleButton>()
                .FirstOrDefault(button => button.Name == "TabSelectButton" && button.IsChecked == true);
            selected?.BringIntoView();
            UpdateTabScrollButtons();
        }, DispatcherPriority.Loaded);
    }

    private void OnTabScrollChanged(object? sender, ScrollChangedEventArgs e) => UpdateTabScrollButtons();
    private void UpdateTabScrollButtons()
    {
        if (ScrollTabsLeftButton is null || ScrollTabsRightButton is null) return;
        ScrollTabsLeftButton.IsEnabled = DocumentTabScroller.Offset.X > 0.5;
        ScrollTabsRightButton.IsEnabled = DocumentTabScroller.Offset.X + DocumentTabScroller.Viewport.Width < DocumentTabScroller.Extent.Width - 0.5;
    }
    private void OnScrollTabsLeft(object? sender, RoutedEventArgs e) =>
        DocumentTabScroller.Offset = DocumentTabScroller.Offset.WithX(Math.Max(0, DocumentTabScroller.Offset.X - DocumentTabWidth));
    private void OnScrollTabsRight(object? sender, RoutedEventArgs e) =>
        DocumentTabScroller.Offset = DocumentTabScroller.Offset.WithX(DocumentTabScroller.Offset.X + DocumentTabWidth);

    private void OnWorkspaceTabClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || sender is not ToggleButton tab) return;

        // Edge does nothing when its current tab is clicked. Keep the checked state
        // because the ToggleButton has already toggled itself before Click is raised.
        if (viewModel.IsWorkspaceTabSelected)
        {
            tab.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            return;
        }

        viewModel.ShowHomeCommand.Execute(null);
    }

    private async void OnWorkspaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (e.Key == Key.W && vm.SelectedDocument is { } current)
        {
            e.Handled = true;
            await vm.CloseDocumentCommand.ExecuteAsync(current);
        }
        else if (e.Key == Key.Tab)
        {
            e.Handled = true;
            int count = vm.OpenDocuments.Count + 1;
            int index = vm.SelectedDocument is null ? 0 : vm.OpenDocuments.IndexOf(vm.SelectedDocument) + 1;
            index = (index + (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? count - 1 : 1)) % count;
            if (index == 0) vm.ShowHomeCommand.Execute(null);
            else vm.SelectDocumentCommand.Execute(vm.OpenDocuments[index - 1]);
        }
    }
}
