using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TuneSync.App.Converters;

/// <summary>Top/bottom padding of half the viewport height, so the first and last lyric lines can
/// also scroll to the vertical center like every other line.</summary>
public sealed class HalfHeightPaddingConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var half = value is double h && h > 0 ? h / 2 : 0;
        return new Thickness(0, half, 0, half);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
