using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using SonicRelay.Windows.Presentation;

namespace SonicRelay.Windows.Desktop.Controls;

/// <summary>
/// A pill showing a status label coloured by its <see cref="DashboardBadge"/> semantic
/// (issue #32 component). The colour mapping lives in the design tokens; this control only
/// exposes <see cref="Text"/> and <see cref="Badge"/> so it can badge any status row.
/// </summary>
public partial class ConnectionStatusBadge : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<ConnectionStatusBadge, string?>(nameof(Text));

    public static readonly StyledProperty<DashboardBadge> BadgeProperty =
        AvaloniaProperty.Register<ConnectionStatusBadge, DashboardBadge>(nameof(Badge));

    public ConnectionStatusBadge() => InitializeComponent();

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public DashboardBadge Badge
    {
        get => GetValue(BadgeProperty);
        set => SetValue(BadgeProperty, value);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
