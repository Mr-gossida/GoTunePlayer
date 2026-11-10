using System.Globalization;
using System.Windows.Data;

namespace TuneSync.App.Converters;

/// <summary>Two-way bridge between an enum-valued property and a RadioButton's IsChecked, so a
/// group of RadioButtons can drive a single enum property (e.g. list/grid view mode).</summary>
public sealed class EnumToBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is not null ? Enum.Parse(targetType, parameter.ToString()!) : Binding.DoNothing;
}
