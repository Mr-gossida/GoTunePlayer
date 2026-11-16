using Microsoft.Extensions.DependencyInjection;
using TuneSync.App.ViewModels;

namespace TuneSync.App;

// The HUD: a small always-on-top card over every other program (HudWindow, HudViewModel).
public partial class ShellWindow
{
    private HudViewModel Hud => _services.GetRequiredService<HudViewModel>();

    public void ToggleHud() => Hud.Toggle();

    private void Hud_Click(object sender, System.Windows.RoutedEventArgs e) => ToggleHud();

    /// <summary>The title bar's HUD button is lit while the HUD shows.</summary>
    private void WatchHud()
    {
        var hud = Hud;
        HudButton.Tag = hud.IsOpen;
        hud.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HudViewModel.IsOpen)) HudButton.Tag = hud.IsOpen;
        };
    }
}
