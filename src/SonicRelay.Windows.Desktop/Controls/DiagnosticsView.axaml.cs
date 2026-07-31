using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SonicRelay.Windows.Desktop.Controls;

/// <summary>Diagnostics destination (issue #32): the publisher event log at full height.
/// Binds to a <c>DashboardShellViewModel</c> DataContext.</summary>
public partial class DiagnosticsView : UserControl
{
    public DiagnosticsView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
