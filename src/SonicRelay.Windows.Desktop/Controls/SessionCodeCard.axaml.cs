using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SonicRelay.Windows.Desktop.ViewModels;

namespace SonicRelay.Windows.Desktop.Controls;

/// <summary>
/// Highlighted session code with a copy affordance (issue #32 component). Binds to a
/// <c>DashboardShellViewModel</c> DataContext and copies the code to the system clipboard
/// itself, so no clipboard plumbing leaks into the view models.
/// </summary>
public partial class SessionCodeCard : UserControl
{
    public SessionCodeCard() => InitializeComponent();

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DashboardShellViewModel vm || !vm.HasSessionCode) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(vm.SessionCodeText);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
