using Avalonia.Controls;

namespace pdf_fluent.Controls;

public sealed class UnselectedMenuFlyout : MenuFlyout
{
    protected override bool ShowAtCore(Control placementTarget, bool showAtPointer = false)
    {
        bool opened = base.ShowAtCore(placementTarget, showAtPointer);
        if (opened && Popup.Child is MenuFlyoutPresenter presenter) presenter.SelectedIndex = -1;
        return opened;
    }
}
