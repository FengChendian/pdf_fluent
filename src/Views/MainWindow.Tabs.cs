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
        {
            UpdateTabSeparators();
            QueueTabUpdate();
        }
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
            // Size from the strip's fixed controls, not the previous viewport width:
            // the viewport shrinks to the tabs so the plus always follows them.
            double available = Math.Max(0, TabStripLayout.Bounds.Width - WorkspaceTopTab.Bounds.Width
                - NewTabActions.Bounds.Width - TabToolbar.Bounds.Width
                - (TabSearchHost.IsVisible ? TabSearchHost.Bounds.Width + TabSearchHost.Margin.Left + TabSearchHost.Margin.Right : 0));
            bool overflow = count * 144 > available;
            ScrollTabsLeftButton.IsVisible = ScrollTabsRightButton.IsVisible = overflow;
            DocumentTabWidth = count == 0 ? 224 : Math.Clamp((available - (overflow ? 56 : 0)) / count, 144, 224);
            // Keep ten-pixel gutters inside the viewport for both end tabs' feet.
            DocumentTabScroller.Width = Math.Min(count * DocumentTabWidth, Math.Max(0, available - (overflow ? 56 : 0))) + 20;
            DocumentTabs.UpdateLayout();
            UpdateTabSeparators();
            var selected = DocumentTabs.GetVisualDescendants().OfType<ToggleButton>()
                .FirstOrDefault(button => button.Name == "TabSelectButton" && button.IsChecked == true);
            if (selected is not null)
                selected.BringIntoView(new Rect(-10, 0, selected.Bounds.Width + 20, selected.Bounds.Height));
            UpdateTabScrollButtons();
        }, DispatcherPriority.Loaded);
    }

    private void OnTabPointerChanged(object? sender, PointerEventArgs e) => UpdateTabSeparators();

    private void UpdateTabSeparators()
    {
        if (_tabViewModel is null) return;
        // Use each whole tab as the hover region, including the independent close button.
        bool previousEmphasized = _tabViewModel.IsWorkspaceTabSelected || WorkspaceTopTab.IsPointerOver;
        WorkspaceTopTab.ZIndex = _tabViewModel.IsWorkspaceTabSelected ? 2 : WorkspaceTopTab.IsPointerOver ? 1 : 0;
        // The workspace tab and the document scroller are siblings. Item ZIndex only
        // orders documents inside the scroller, so raise that branch for selection too.
        DocumentTabScroller.ZIndex = _tabViewModel.IsWorkspaceTabSelected ? 0 : 2;
        foreach (var presenter in DocumentTabs.GetRealizedContainers())
        {
            var tab = presenter.GetVisualDescendants().OfType<ToggleButton>()
                .FirstOrDefault(button => button.Name == "TabSelectButton");
            if (tab?.DataContext is not DocumentTabViewModel document ||
                tab.GetVisualParent() is not Grid grid || grid.GetVisualParent() is not Border root) continue;

            bool selected = ReferenceEquals(_tabViewModel.SelectedDocument, document);
            bool emphasized = selected || root.IsPointerOver;
            tab.Classes.Set("tab-hover", root.IsPointerOver);
            var separator = grid.Children.OfType<Border>().Single(border => border.Name == "TabSeparator");
            separator.IsVisible = !previousEmphasized && !emphasized;
            // Selected feet paint above the neighboring inactive tab's surface.
            presenter.ZIndex = selected ? 2 : root.IsPointerOver ? 1 : 0;
            previousEmphasized = emphasized;
        }
        NewTabSeparator.IsVisible = !previousEmphasized;
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
