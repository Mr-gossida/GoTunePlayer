using System.Linq;
using System.Windows;

namespace TuneSync.App;

/// <summary>Non-blocking toast notifications for errors and notices, dismissed automatically:
/// small capsules drawn by <see cref="ToastHost"/>. Each window registers its own host and a
/// toast appears in whichever window is active (a tool window, or else the main player). Safe to
/// call from any thread. Yes/no decisions before destructive actions still use <see cref="Dialogs"/>.</summary>
public static class Toast
{
    private static readonly Dictionary<Window, ToastHost> Hosts = [];

    public static void Register(Window window, ToastHost host)
    {
        Hosts[window] = host;
        window.Closed += (_, _) => Hosts.Remove(window);
    }

    public static void Error(string title, string message) => Show(ToastKind.Error, title, message, seconds: 7);

    public static void Warning(string title, string message) => Show(ToastKind.Warning, title, message, seconds: 6);

    public static void Success(string title, string message) => Show(ToastKind.Success, title, message, seconds: 4);

    public static void Info(string title, string message) => Show(ToastKind.Info, title, message, seconds: 4);

    private static void Show(ToastKind kind, string title, string message, int seconds)
    {
        if (Application.Current is not { } app) return;
        app.Dispatcher.BeginInvoke(() =>
        {
            var target = Hosts.FirstOrDefault(h => h.Key.IsActive).Value
                         ?? (app.MainWindow is { } main && Hosts.TryGetValue(main, out var mainHost) ? mainHost : null)
                         ?? Hosts.Values.FirstOrDefault();
            // longer text stays a little longer, so it can be read to the end
            var read = TimeSpan.FromSeconds(seconds + Math.Min(4, (title.Length + message.Length) / 40.0));
            target?.Show(kind, title, message, read);
        });
    }
}
