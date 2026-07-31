using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SonicRelay.Windows.Desktop.Controls;

/// <summary>
/// Left navigation rail (issue #32 component). Renders the destinations from a
/// <c>MainWindowViewModel</c> DataContext. Only the Dashboard surface is live this phase;
/// the remaining entries are disabled placeholders for the later slices.
/// </summary>
public partial class SidebarNavigation : UserControl
{
    public SidebarNavigation() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
