using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SonicRelay.Windows.Desktop.Controls;

/// <summary>Audio destination (issue #32): pick the system output to capture. Binds to an
/// <c>AudioPageViewModel</c>; the selection flows into the platform enumerator + preference store.</summary>
public partial class AudioView : UserControl
{
    public AudioView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
