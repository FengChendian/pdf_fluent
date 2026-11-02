using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using pdf_fluent.ViewModels;

namespace pdf_fluent.Views;

public partial class PdfDocumentView : UserControl
{
    public PdfDocumentView()
    {
        InitializeComponent();
        AddHandler(PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Tunnel);
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
            DataContext is not DocumentTabViewModel document)
        {
            return;
        }

        e.Handled = true;
        if (e.Delta.Y > 0) document.ZoomInCommand.Execute(null);
        else if (e.Delta.Y < 0) document.ZoomOutCommand.Execute(null);
    }

    private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (DataContext is DocumentTabViewModel document &&
            e.Container.DataContext is PdfPageViewModel page)
        {
            document.EnsurePageRendered(page);
        }
    }

    private void OnContainerClearing(object? sender, ContainerClearingEventArgs e)
    {
        if (DataContext is DocumentTabViewModel document &&
            e.Container.DataContext is PdfPageViewModel page)
        {
            document.ReleasePage(page);
        }
    }
}
