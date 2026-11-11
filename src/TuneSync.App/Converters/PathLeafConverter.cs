using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace TuneSync.App.Converters;

/// <summary>Shows just the last folder of a full path ("\\nas\Music\KPop" → "KPop"); any other
/// text (an artist or album name, which may itself contain '/', like "AC/DC") passes through.</summary>
public sealed class PathLeafConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = value?.ToString() ?? "";
        if (!Path.IsPathRooted(text)) return text;
        var leaf = Path.GetFileName(text.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(leaf) ? text : leaf;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
