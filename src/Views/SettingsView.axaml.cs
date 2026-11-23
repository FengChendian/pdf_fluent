using Avalonia.Controls;
using pdf_fluent.Diagnostics;

namespace pdf_fluent.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        StartupTrace.Mark("SettingsView.ctor.enter");
        InitializeComponent();
        StartupTrace.Mark("SettingsView.ctor.exit");
    }
}
