using System.Windows;
using System.Windows.Media;
using TuneSync.Core.Models;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace TuneSync.App;

public static class ThemeHelper
{
    /// <summary>Applies the chosen theme app-wide. "System" also keeps following Windows' own
    /// light/dark setting while the app runs; an explicit choice stops following it.</summary>
    public static void Apply(AppTheme theme, Window? window = null)
    {
        switch (theme)
        {
            case AppTheme.Light:
                ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.Mica, true);
                break;
            case AppTheme.Dark:
                ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica, true);
                break;
            default:
                ApplicationThemeManager.ApplySystemTheme(true);
                break;
        }
        ApplyEditColors();

        if (window is null) return;
        if (theme == AppTheme.System)
            SystemThemeWatcher.Watch(window, WindowBackdropType.Mica, true);
        else if (window.IsLoaded)
            SystemThemeWatcher.UnWatch(window); // throws for a window that isn't loaded yet (nothing to unwatch then)
    }

    private static bool _followingChanges;

    /// <summary>The tag editor's "changed, not saved yet" colors. Fixed blues (Windows' default
    /// accent) rather than the system accent, which may be a gray that looks like disabled text.</summary>
    private static void ApplyEditColors()
    {
        if (!_followingChanges)
        {
            _followingChanges = true;
            ApplicationThemeManager.Changed += (_, _) => ApplyEditColors();
        }
        if (Application.Current is not { } app) return;
        var dark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
        var color = dark ? Color.FromRgb(0x60, 0xCD, 0xFF) : Color.FromRgb(0x00, 0x5F, 0xB8);
        app.Resources["TagChangeBrush"] = Frozen(new SolidColorBrush(color));
        app.Resources["TagChangeBackgroundBrush"] = Frozen(new SolidColorBrush(Color.FromArgb(dark ? (byte)0x1F : (byte)0x14, color.R, color.G, color.B)));
        app.Resources["TagChangeOnBrush"] = Frozen(new SolidColorBrush(dark ? Colors.Black : Colors.White));
    }

    private static SolidColorBrush Frozen(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
