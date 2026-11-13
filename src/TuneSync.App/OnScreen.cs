using System.Windows;

namespace TuneSync.App;

/// <summary>Whether an animated visual (spectrum, seek bar waves, equalizer icon) can actually be
/// seen. WPF keeps an element "visible" while its window is minimized, so per-frame drawing went
/// on in the background - and any per-frame handler also keeps WPF rendering the whole app at
/// the monitor's refresh rate. Those visuals pause until their window is back.</summary>
internal static class OnScreen
{
    public static bool Check(FrameworkElement element)
    {
        if (!element.IsVisible) return false;
        var window = Window.GetWindow(element);
        return window is null || window.WindowState != WindowState.Minimized;
    }

    /// <summary>Calls <paramref name="changed"/> when the element's window is minimized or
    /// restored (and when it becomes visible or hidden), for as long as the element is loaded.</summary>
    public static void Watch(FrameworkElement element, Action changed)
    {
        Window? window = null;
        void OnState(object? sender, EventArgs e) => changed();
        element.Loaded += (_, _) =>
        {
            if (window is not null) window.StateChanged -= OnState;
            window = Window.GetWindow(element);
            if (window is not null) window.StateChanged += OnState;
        };
        element.Unloaded += (_, _) =>
        {
            if (window is not null) window.StateChanged -= OnState;
            window = null;
        };
        element.IsVisibleChanged += (_, _) => changed();
    }
}
