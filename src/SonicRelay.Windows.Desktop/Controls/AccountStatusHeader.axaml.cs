using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SonicRelay.Windows.Desktop.Controls;

/// <summary>
/// Account area and global transmission status for the top bar (issue #32 component). The
/// account fields come from the <c>DashboardShellViewModel</c> DataContext. The adjacent
/// "Unpair" button in <c>MainWindow.axaml</c> (bound to <c>MainWindowViewModel.UnpairCommand</c>)
/// revokes this device's pairings, forgets its identity, and returns to the pairing surface —
/// the recovery path for a stale/rejected device credential (issue #26 follow-up).
/// </summary>
public partial class AccountStatusHeader : UserControl
{
    public AccountStatusHeader() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
