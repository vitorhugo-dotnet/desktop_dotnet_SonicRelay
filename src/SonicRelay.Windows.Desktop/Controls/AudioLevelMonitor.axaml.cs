using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SonicRelay.Windows.Desktop.Controls;

/// <summary>
/// Real-time audio level card (issue #32 component). Shows the captured system output's
/// peak level as a meter plus a dB read-out. Bound to the real capture level from the
/// presentation projection; it holds no audio logic of its own.
/// </summary>
public partial class AudioLevelMonitor : UserControl
{
    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<AudioLevelMonitor, double>(nameof(Level));

    public static readonly StyledProperty<string?> PeakTextProperty =
        AvaloniaProperty.Register<AudioLevelMonitor, string?>(nameof(PeakText));

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<AudioLevelMonitor, bool>(nameof(IsActive));

    public AudioLevelMonitor() => InitializeComponent();

    /// <summary>Peak level, 0..1.</summary>
    public double Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public string? PeakText
    {
        get => GetValue(PeakTextProperty);
        set => SetValue(PeakTextProperty, value);
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
