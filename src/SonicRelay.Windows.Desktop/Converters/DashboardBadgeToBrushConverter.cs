using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using SonicRelay.Windows.Presentation;

namespace SonicRelay.Windows.Desktop.Converters;

/// <summary>
/// Maps a <see cref="DashboardBadge"/> to one of the semantic status brushes defined in
/// <c>Styles/Tokens.axaml</c>, resolved for the active theme variant. The converter
/// parameter selects the role: <c>Foreground</c> (default), <c>Background</c> or
/// <c>Border</c>. Keeping the mapping here means components never hard-code status colors.
/// </summary>
public sealed class DashboardBadgeToBrushConverter : IValueConverter
{
    public static readonly DashboardBadgeToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DashboardBadge badge) return null;
        var role = parameter as string ?? "Foreground";
        var key = ResourceKey(badge, role);
        return ResolveBrush(key);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static string ResourceKey(DashboardBadge badge, string role)
    {
        var name = badge switch
        {
            DashboardBadge.Success => "Success",
            DashboardBadge.Warning => "Warning",
            DashboardBadge.Danger => "Danger",
            _ => "Neutral",
        };
        return role switch
        {
            "Background" => $"Sonic.{name}BackgroundBrush",
            "Border" => $"Sonic.{name}BorderBrush",
            _ => $"Sonic.{name}Brush",
        };
    }

    private static IBrush? ResolveBrush(string key)
    {
        var app = Application.Current;
        if (app is null) return null;
        return app.TryGetResource(key, app.ActualThemeVariant, out var resource) && resource is IBrush brush
            ? brush
            : null;
    }
}
