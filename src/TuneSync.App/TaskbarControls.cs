using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shell;
using TuneSync.App.ViewModels;

namespace TuneSync.App;

/// <summary>Previous / play-pause / next buttons under the window's taskbar thumbnail (like
/// Windows Media Player and Spotify), and the playing song as the window title, which is what
/// the taskbar and Alt+Tab show.</summary>
public static class TaskbarControls
{
    public static void Attach(Window window, PlaybackViewModel playback)
    {
        var previous = new ThumbButtonInfo { Description = "이전 곡", Command = playback.PlayPreviousCommand, ImageSource = Glyph("\uE892") };
        var playPause = new ThumbButtonInfo { Command = playback.TogglePlayPauseCommand };
        var next = new ThumbButtonInfo { Description = "다음 곡", Command = playback.PlayNextCommand, ImageSource = Glyph("\uE893") };
        window.TaskbarItemInfo = new TaskbarItemInfo { ThumbButtonInfos = [previous, playPause, next] };

        void Refresh()
        {
            playPause.ImageSource = Glyph(playback.IsPlaying ? "\uE769" : "\uE768");
            playPause.Description = playback.IsPlaying ? "일시정지" : "재생";
        }
        Refresh();

        window.SetBinding(Window.TitleProperty, new Binding(nameof(PlaybackViewModel.CurrentFile))
        {
            Source = playback,
            Converter = new TitleConverter(),
        });

        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (e.PropertyName == nameof(PlaybackViewModel.IsPlaying)) Refresh();
        };
        playback.PropertyChanged += handler;
        window.Closed += (_, _) => playback.PropertyChanged -= handler;
    }

    /// <summary>A white icon from the Windows icon font (Segoe Fluent Icons on Windows 11,
    /// Segoe MDL2 Assets before it), sized for the taskbar thumbnail toolbar.</summary>
    private static ImageSource Glyph(string glyph)
    {
        var typeface = new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 16, Brushes.White, 1.0);
        var geometry = text.BuildGeometry(new Point(0, 0));
        var drawing = new GeometryDrawing(Brushes.White, null, geometry);
        var group = new DrawingGroup();
        // Pad to a square so every icon sits centered at the same size.
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        group.Children.Add(drawing);
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private sealed class TitleConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is LibraryFileViewModel file
                ? string.IsNullOrWhiteSpace(file.Artist) ? $"{file.DisplayTitle} · TuneSync" : $"{file.DisplayTitle} - {file.Artist} · TuneSync"
                : "TuneSync";

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
