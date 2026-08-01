using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SonicRelay.Windows.Desktop.Controls;

/// <summary>
/// Account area and global transmission status for the top bar (issue #32 component). The
/// account fields come from the <c>DashboardShellViewModel</c> DataContext. There is no
/// sign-out action in the device-identity model (issue #26) — a paired device stays paired
/// until revoked from the pairing surface or a viewer's own device management.
/// </summary>
public partial class AccountStatusHeader : UserControl
{
    public AccountStatusHeader() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
