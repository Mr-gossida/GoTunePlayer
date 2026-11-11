using System.Globalization;
using System.Windows.Data;
using TuneSync.Core.Models;
using Wpf.Ui.Controls;

namespace TuneSync.App.Converters;

/// <summary>Icon for a playlist group header: folder, artist or album.</summary>
public sealed class GroupIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        PlaylistGroupKey.Folder => SymbolRegular.Folder24,
        PlaylistGroupKey.Artist => SymbolRegular.Person24,
        PlaylistGroupKey.Album => SymbolRegular.MusicNote224,
        _ => SymbolRegular.List24,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
