using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SonicRelay.Windows.Desktop.Controls;

/// <summary>
/// Account area and global transmission status for the top bar (issue #32 component). The
/// account fields come from the <c>DashboardShellViewModel</c> DataContext; the sign-out
/// action is supplied by the host through <see cref="SignOutCommand"/>.
/// </summary>
public partial class AccountStatusHeader : UserControl
{
    public static readonly StyledProperty<ICommand?> SignOutCommandProperty =
        AvaloniaProperty.Register<AccountStatusHeader, ICommand?>(nameof(SignOutCommand));

    public AccountStatusHeader() => InitializeComponent();

    public ICommand? SignOutCommand
    {
        get => GetValue(SignOutCommandProperty);
        set => SetValue(SignOutCommandProperty, value);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
