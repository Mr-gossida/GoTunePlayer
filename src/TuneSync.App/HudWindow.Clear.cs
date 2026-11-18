using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using TuneSync.App.Views;

namespace TuneSync.App;

// 클리어 모드 (설정 › HUD 클리어 모드, or the HUD's menu): no card behind the HUD, only its words,
// cover, spectrum and buttons - fully solid, white, and each with a dark halo so it reads over a
// white page as well as a dark editor or a film.
public partial class HudWindow
{
    /// <summary>The halo around each word, cover and button: a soft dark shadow right under it.
    /// One frozen effect shared by all of them; it is only redrawn when the thing it surrounds
    /// changes (a new lyric line, the time each second), never on every frame - the spectrum,
    /// which changes every frame, gets a plain dark copy behind it instead (see the XAML).</summary>
    private static readonly Effect Halo = Frozen(new DropShadowEffect
    {
        Color = Colors.Black, ShadowDepth = 0, BlurRadius = 4, Opacity = 1, RenderingBias = RenderingBias.Performance,
    });

    /// <summary>The text colors inside the layouts while clear: white on any background (the halo
    /// gives the contrast). Set on the layouts only, so the menu keeps the theme's colors.</summary>
    private static readonly (string Key, Brush Brush)[] ClearText =
    [
        ("TextFillColorPrimaryBrush", Frozen(new SolidColorBrush(Colors.White))),
        ("TextFillColorSecondaryBrush", Frozen(new SolidColorBrush(Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF)))),
        ("TextFillColorTertiaryBrush", Frozen(new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)))),
    ];

    private FrameworkElement[] Layouts => [CardLayout, CompactLayout, LyricsLayout, VerticalLayout];

    /// <summary>클리어 on or off (from the constructor and whenever the setting changes).</summary>
    public void ApplyClear()
    {
        var clear = _settings.Hud.Clear;
        foreach (var layout in Layouts)
        {
            foreach (var (key, brush) in ClearText)
            {
                if (clear) layout.Resources[key] = brush;
                else layout.Resources.Remove(key);
            }
            foreach (var element in HaloTargets(layout)) element.Effect = clear ? Halo : null;
            // a second halo around panels of words only: two layers make an edge that holds on a
            // white page, where one soft shadow alone fades out too soon
            foreach (var panel in TextPanels(layout)) panel.Effect = clear ? Halo : null;
        }
        SetSurface(_hovered || _pointerOverCard, 0.2);
        if (!_hovered && !_pointerOverCard && IsVisible) Fade(Idle, 0.12);
    }

    /// <summary>The card's surface: there as usual; when clear, gone - or faintly there while the
    /// pointer is over the HUD, to show where it is and make its buttons easy to find.</summary>
    private void SetSurface(bool pointerOver, double seconds)
    {
        var to = !_settings.Hud.Clear ? 1 : pointerOver ? 0.75 : 0;
        Surface.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromSeconds(seconds)));
    }

    /// <summary>Everything that gets the halo: words, covers and buttons (each on its own, so the
    /// spectrum and the progress line beside them aren't drawn into anyone's effect).</summary>
    private static IEnumerable<UIElement> HaloTargets(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            switch (child)
            {
                case SpectrumView or ProgressBar:
                    continue;
                case TextBlock or ButtonBase or ContentControl { Focusable: false }:
                    yield return (UIElement)child; // a button's or the cover's insides come with it
                    continue;
            }
            foreach (var inner in HaloTargets(child)) yield return inner;
        }
    }

    /// <summary>The outermost panels and borders that hold only still things - words, buttons,
    /// the cover - and so never anything redrawn on every frame.</summary>
    private static IEnumerable<FrameworkElement> TextPanels(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is Panel or Border && IsStill((UIElement)child)) yield return (FrameworkElement)child;
            else if (child is not (TextBlock or ButtonBase or ContentControl or SpectrumView or ProgressBar))
                foreach (var inner in TextPanels(child)) yield return inner;
        }
    }

    private static bool IsStill(UIElement element) => element switch
    {
        TextBlock or ButtonBase or ContentControl => true,
        Panel panel => panel.Children.Count > 0 && panel.Children.Cast<UIElement>().All(IsStill),
        Border { Child: { } child } => IsStill(child),
        _ => false,
    };

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
