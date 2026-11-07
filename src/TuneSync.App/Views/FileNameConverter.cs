using System.Globalization;
using System.Windows.Data;

namespace TuneSync.App.Views;

public sealed class FileNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is string path ? System.IO.Path.GetFileName(path) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
