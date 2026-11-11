using System.Globalization;
using System.Windows.Data;

namespace TuneSync.App.Converters;

/// <summary>Shows "—" for an empty tag value instead of a blank gap.</summary>
public sealed class EmptyDashConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string s && !string.IsNullOrWhiteSpace(s) ? s : "—";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
