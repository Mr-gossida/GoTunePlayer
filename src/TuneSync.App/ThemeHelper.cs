using System.Windows;
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

        if (window is null) return;
        if (theme == AppTheme.System)
            SystemThemeWatcher.Watch(window, WindowBackdropType.Mica, true);
        else if (window.IsLoaded)
            SystemThemeWatcher.UnWatch(window); // throws for a window that isn't loaded yet (nothing to unwatch then)
    }
}
