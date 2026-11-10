using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TuneSync.App.Converters;

/// <summary>Visible when the bound enum value's ToString() matches the converter parameter,
/// Collapsed otherwise. Used to swap the list/grid containers off a single ViewMode property.</summary>
public sealed class EnumToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString() ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
