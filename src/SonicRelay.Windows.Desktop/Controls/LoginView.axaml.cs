using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SonicRelay.Windows.Desktop.Controls;

/// <summary>
/// Sign-in / registration surface (issue #32). Binds to an <c>AuthViewModel</c> DataContext;
/// it holds no auth logic — submitting forwards through the view model to the publisher
/// workflow, and errors come back from the workflow snapshot.
/// </summary>
public partial class LoginView : UserControl
{
    public LoginView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
