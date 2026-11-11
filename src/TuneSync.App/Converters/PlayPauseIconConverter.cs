using System.Globalization;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace TuneSync.App.Converters;

public sealed class PlayPauseIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new SymbolIcon { Symbol = value is true ? SymbolRegular.Pause24 : SymbolRegular.Play24, Filled = true };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
