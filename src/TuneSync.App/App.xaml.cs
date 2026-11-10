using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TuneSync.App.ViewModels;
using TuneSync.App.Views;
using TuneSync.Core.Services;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Appearance;

namespace TuneSync.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((_, services) => ConfigureServices(services))
            .Build();

        // Theme/accent must be applied before any Wpf.Ui control is constructed, so this has
        // to run before the host-resolved window is shown.
        ApplicationThemeManager.Apply(ApplicationTheme.Light);
        ApplicationAccentColorManager.ApplySystemAccent();

        _host.Start();
        _host.Services.GetRequiredService<ShellWindow>().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<INavigationViewPageProvider, AppNavigationPageProvider>();
        services.AddSingleton<ShellWindow>();

        services.AddSingleton<IMusicBrainzService, MusicBrainzService>();
        services.AddSingleton<ILyricsService, LrcLibLyricsService>();
        services.AddSingleton<IAutoFillService, AutoFillService>();

        // Singleton so the scanned file list (and selection) survives navigating between pages.
        services.AddSingleton<LibraryViewModel>();
        services.AddTransient<AutoMatchViewModel>();
        services.AddTransient<MergeViewModel>();
        services.AddTransient<RenameViewModel>();

        services.AddTransient<LibraryView>();
        services.AddTransient<AutoMatchView>();
        services.AddTransient<MergeView>();
        services.AddTransient<RenameView>();
        services.AddTransient<SettingsView>();
    }
}
