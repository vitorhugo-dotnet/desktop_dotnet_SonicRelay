using System.Globalization;
using Avalonia.Data.Converters;

namespace SonicRelay.Windows.Desktop.Controls;

public sealed class ClearLogsLabelConverter : IValueConverter
{
    public static readonly ClearLogsLabelConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "Confirm clear?" : "Clear logs";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
