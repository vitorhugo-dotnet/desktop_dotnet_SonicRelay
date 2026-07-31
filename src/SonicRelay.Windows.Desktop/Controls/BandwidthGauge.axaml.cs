using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SonicRelay.Windows.Desktop.Controls;

/// <summary>
/// Bandwidth / bitrate gauge with the active codec label (issue #32 component). Bitrate is
/// shown as "—" until the WebRTC getStats wiring lands (next phase-2 slice); the codec is a
/// real, stable fact and is shown when capturing.
/// </summary>
public partial class BandwidthGauge : UserControl
{
    public BandwidthGauge() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
