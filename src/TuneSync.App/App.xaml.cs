using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TuneSync.App.ViewModels;
using TuneSync.App.Views;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private IHost? _host;

    /// <summary>The app's services, for windows opened outside dependency injection.</summary>
    public IServiceProvider Services => _host!.Services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((_, services) => ConfigureServices(services))
            .Build();

        try
        {
            // Must run before any Wpf.Ui control is constructed, so the first frame is already in
            // the right theme instead of flashing light and then switching.
            ThemeHelper.Apply(_host.Services.GetRequiredService<AppSettings>().Theme);

            PlayerPalette.Register(this);
            _host.Start();
            var shell = _host.Services.GetRequiredService<ShellWindow>();
            MainWindow = shell;
            var settings = _host.Services.GetRequiredService<AppSettings>();
            // Started by Windows at sign-in (Settings › Windows 시작 시 자동 실행): quietly, minimized.
            var autoStarted = e.Args.Contains(AutoStart.Argument, StringComparer.OrdinalIgnoreCase);
            if (settings.PlayerMode == PlayerMode.Full)
            {
                if (autoStarted) shell.WindowState = WindowState.Minimized;
                shell.Show();
            }
            else
            {
                shell.EnterMiniMode(settings.PlayerMode); // reopen in the mini player last used
            }
            try { AutoStart.Refresh(); } catch { /* registry unavailable - the toggle shows its state */ }
        }
        catch (Exception ex)
        {
            // Without a main window the app would otherwise linger invisibly in the background.
            LogError(ex);
            MessageBox.Show($"TuneSync를 시작하지 못했습니다.\n\n{ex.Message}", "TuneSync",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Last-resort safety net: report the error and keep running instead of letting one
    /// unexpected exception (a locked file, a network share dropping out) close the whole app.</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        LogError(e.Exception);
        Toast.Error("예기치 않은 오류", e.Exception.Message);
    }

    /// <summary>Appends the full exception to %LocalAppData%\TuneSync\error.log, so a problem a
    /// user reports can actually be diagnosed afterwards.</summary>
    internal static void LogError(Exception ex)
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TuneSync");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // logging must never be the thing that breaks
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ShellWindow>();

        services.AddSingleton(_ => AppSettingsStore.Load());
        services.AddSingleton<Network.NetworkLibrary>();

        services.AddSingleton<IMusicBrainzService, MusicBrainzService>();
        services.AddSingleton<ILyricsService, LrcLibLyricsService>();
        services.AddSingleton<IAutoFillService, AutoFillService>();

        // Singletons: closing a tool window and opening it again must bring back the same
        // selections/previews/typed-in values, so every page and view model lives for the app.
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<AutoMatchViewModel>();
        services.AddSingleton<TagEditorViewModel>();
        services.AddSingleton<PlaybackViewModel>();
        services.AddSingleton<LibraryPageViewModel>();
        services.AddSingleton<PlayPageViewModel>();
        services.AddSingleton<MergeViewModel>();
        services.AddSingleton<RenameViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<SleepTimerViewModel>();

        services.AddSingleton<LibraryView>();
        services.AddSingleton<PlayView>();
        services.AddSingleton<MergeView>();
        services.AddSingleton<RenameView>();
        services.AddSingleton<SettingsView>();
    }
}
