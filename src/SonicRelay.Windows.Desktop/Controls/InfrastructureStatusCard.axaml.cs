using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SonicRelay.Windows.Desktop.Controls;

/// <summary>
/// Session / Signaling / WebRTC-ICE status, connection mode and viewer count (issue #32
/// component). Binds to a <c>DashboardShellViewModel</c> DataContext; it renders state only.
/// </summary>
public partial class InfrastructureStatusCard : UserControl
{
    public InfrastructureStatusCard() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
