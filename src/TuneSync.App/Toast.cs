using System.Linq;
using System.Windows;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace TuneSync.App;

/// <summary>Non-blocking toast notifications (WPF-UI snackbars) for errors and notices, dismissed
/// automatically. Each window registers its own presenter and a toast appears in whichever window
/// is active (a tool window, or else the main player). Safe to call from any thread. Yes/no
/// decisions before destructive actions still use <see cref="Dialogs"/>.</summary>
public static class Toast
{
    private static readonly Dictionary<Window, ISnackbarService> Services = [];

    public static void Register(Window window, ISnackbarService service)
    {
        Services[window] = service;
        window.Closed += (_, _) => Services.Remove(window);
    }

    public static void Error(string title, string message) =>
        Show(title, message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24, seconds: 7);

    public static void Warning(string title, string message) =>
        Show(title, message, ControlAppearance.Caution, SymbolRegular.Warning24, seconds: 6);

    public static void Success(string title, string message) =>
        Show(title, message, ControlAppearance.Success, SymbolRegular.CheckmarkCircle24, seconds: 4);

    public static void Info(string title, string message) =>
        Show(title, message, ControlAppearance.Info, SymbolRegular.Info24, seconds: 4);

    private static void Show(string title, string message, ControlAppearance appearance, SymbolRegular icon, int seconds)
    {
        if (Application.Current is not { } app) return;
        app.Dispatcher.BeginInvoke(() =>
        {
            var target = Services.FirstOrDefault(s => s.Key.IsActive).Value
                         ?? (app.MainWindow is { } main && Services.TryGetValue(main, out var mainService) ? mainService : null)
                         ?? Services.Values.FirstOrDefault();
            target?.Show(title, message, appearance, new SymbolIcon { Symbol = icon }, TimeSpan.FromSeconds(seconds));
        });
    }
}
