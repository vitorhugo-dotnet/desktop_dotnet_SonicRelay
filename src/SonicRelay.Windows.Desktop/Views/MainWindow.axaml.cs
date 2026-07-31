using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SonicRelay.Windows.Desktop.Views;

/// <summary>
/// The shared Avalonia shell window (issue #32, phase 2). It composes the reusable shell
/// components and binds them to a <see cref="ViewModels.MainWindowViewModel"/>; all display
/// state flows from the shared presentation projection.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
