using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SonicRelay.Windows.Desktop.Controls;

/// <summary>Session destination (issue #32): broadcast code, infrastructure and transmission
/// details. Binds to a <c>DashboardShellViewModel</c> DataContext; renders state only.</summary>
public partial class SessionView : UserControl
{
    public SessionView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
