using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SonicRelay.Windows.Desktop.Controls;

/// <summary>Settings destination (issue #32): backend, force-relay and audio quality. Binds to a
/// <c>SettingsViewModel</c>; edits flow into the shared preference stores.</summary>
public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
