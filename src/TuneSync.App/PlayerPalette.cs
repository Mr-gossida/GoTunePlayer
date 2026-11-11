using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Wpf.Ui.Appearance;

namespace TuneSync.App;

/// <summary>The player's colors, taken from the playing song's album cover (like Apple Music /
/// Samsung Music): a deep tint for the background and a matching accent for the play button,
/// waveform and now-playing marks. Exposed as app-wide DynamicResources so the main window, its
/// pages and the mini players all pick them up, and animated so a track change blends over.</summary>
public static class PlayerPalette
{
    public const string TintKey = "PlayerTintBrush";
    public const string AccentKey = "PlayerAccentBrush";
    public const string OnAccentKey = "PlayerOnAccentBrush";
    public const string PanelKey = "PlayerPanelBrush";
    public const string TrackKey = "PlayerTrackBrush";
    public const string HoverKey = "PlayerHoverBrush";

    private static Color? _source;

    public static void Register(Application app)
    {
        Apply(null, animate: false);
        ApplicationThemeManager.Changed += (_, _) => Apply(_source, animate: false);
    }

    /// <summary>Recolors the player from a cover's key color; null (no cover / no color in it)
    /// falls back to the Windows accent color.</summary>
    public static void Apply(Color? source, bool animate = true)
    {
        _source = source;
        var dark = ApplicationThemeManager.GetAppTheme() != ApplicationTheme.Light;
        var key = source ?? SystemAccent();
        var (h, s, _) = ToHsl(key);
        var neutral = s < 0.12;

        Color tint, accent, onAccent;
        if (dark)
        {
            tint = FromHsl(h, neutral ? 0.04 : Math.Min(s, 0.42), 0.12);
            accent = neutral ? Color.FromRgb(0xF2, 0xF2, 0xF2) : FromHsl(h, Math.Clamp(s, 0.45, 0.80), 0.76);
            onAccent = neutral ? Color.FromRgb(0x14, 0x14, 0x14) : FromHsl(h, 0.55, 0.12);
        }
        else
        {
            tint = FromHsl(h, neutral ? 0.05 : Math.Min(s, 0.40), 0.93);
            accent = neutral ? Color.FromRgb(0x24, 0x24, 0x24) : FromHsl(h, Math.Clamp(s, 0.50, 0.80), 0.36);
            onAccent = Colors.White;
        }

        Set(TintKey, tint, animate);
        Set(AccentKey, accent, animate);
        Set(OnAccentKey, onAccent, animate);
        Set(PanelKey, dark ? Color.FromArgb(0x38, 0, 0, 0) : Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), animate: false);
        Set(TrackKey, dark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x30, 0, 0, 0), animate: false);
        Set(HoverKey, dark ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x10, 0, 0, 0), animate: false);
        SetScrim(tint, animate);
    }

    public const string ScrimKey = "PlayerScrimBrush";
    private static readonly (double Offset, byte Alpha)[] ScrimStops = [(0, 0x8C), (0.55, 0xB3), (1, 0xEB)];

    /// <summary>The tint deepening toward the bottom over the cover backdrop, as one gradient
    /// fill. (An OpacityMask did the same but cost an extra full-window pass on every frame,
    /// which showed as stutter while resizing.)</summary>
    private static void SetScrim(Color tint, bool animate)
    {
        if (Application.Current is not { } app) return;
        var current = app.Resources[ScrimKey] as LinearGradientBrush;
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        for (var i = 0; i < ScrimStops.Length; i++)
        {
            var (offset, alpha) = ScrimStops[i];
            var target = Color.FromArgb(alpha, tint.R, tint.G, tint.B);
            var stop = new GradientStop(animate && current is not null ? current.GradientStops[i].Color : target, offset);
            if (animate && current is not null)
                stop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(target, TimeSpan.FromMilliseconds(700))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                });
            brush.GradientStops.Add(stop);
        }
        if (!animate || current is null) brush.Freeze();
        app.Resources[ScrimKey] = brush;
    }

    /// <summary>Brushes in the app's resources get frozen, so each change puts in a new brush
    /// (DynamicResource users follow it). An animating brush can't be frozen, so the blend starts
    /// before it goes in.</summary>
    private static void Set(string key, Color color, bool animate)
    {
        if (Application.Current is not { } app) return;
        var current = app.Resources[key] as SolidColorBrush;
        if (current?.Color == color) return;
        if (!animate || current is null)
        {
            var still = new SolidColorBrush(color);
            still.Freeze();
            app.Resources[key] = still;
            return;
        }

        var brush = new SolidColorBrush(current.Color);
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(color, TimeSpan.FromMilliseconds(700))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        });
        app.Resources[key] = brush;
    }

    private static Color SystemAccent() =>
        Application.Current?.TryFindResource("SystemAccentColor") is Color c ? c : Color.FromRgb(0x4C, 0xC2, 0xFF);

    /// <summary>The cover's most characteristic color: the strongest hue among reasonably
    /// saturated, mid-bright pixels (so a big dark or white area doesn't win), or null for a
    /// black-and-white cover. Runs on any thread for a frozen bitmap.</summary>
    public static Color? ExtractKeyColor(BitmapSource cover)
    {
        try
        {
            var scaled = new TransformedBitmap(cover, new ScaleTransform(48.0 / cover.PixelWidth, 48.0 / cover.PixelHeight));
            var bgra = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
            var w = bgra.PixelWidth;
            var hgt = bgra.PixelHeight;
            var pixels = new byte[w * hgt * 4];
            bgra.CopyPixels(pixels, w * 4, 0);

            const int bins = 36;
            var weight = new double[bins];
            var sumR = new double[bins];
            var sumG = new double[bins];
            var sumB = new double[bins];
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var c = Color.FromRgb(pixels[i + 2], pixels[i + 1], pixels[i]);
                var (ch, cs, cl) = ToHsl(c);
                if (cs < 0.18 || cl < 0.12 || cl > 0.92) continue;
                var wgt = cs * (1 - Math.Abs(cl - 0.5) * 1.4);
                var bin = (int)(ch / 360 * bins) % bins;
                weight[bin] += wgt;
                sumR[bin] += c.R * wgt;
                sumG[bin] += c.G * wgt;
                sumB[bin] += c.B * wgt;
            }

            // Neighbouring hue bins belong together (a red cover spans 2-3 bins).
            var best = -1;
            var bestScore = 0.0;
            for (var b = 0; b < bins; b++)
            {
                var score = weight[b] + 0.5 * (weight[(b + 1) % bins] + weight[(b + bins - 1) % bins]);
                if (score > bestScore) { bestScore = score; best = b; }
            }
            if (best < 0 || bestScore < pixels.Length / 4 * 0.02) return null;

            return Color.FromRgb((byte)(sumR[best] / weight[best]), (byte)(sumG[best] / weight[best]), (byte)(sumB[best] / weight[best]));
        }
        catch
        {
            return null;
        }
    }

    private static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max - min < 1e-6) return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        return (h * 60, s, l);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }

        if (s <= 0) { var v = (byte)(l * 255); return Color.FromRgb(v, v, v); }
        var q2 = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p2 = 2 * l - q2;
        var hk = h / 360;
        return Color.FromRgb((byte)(Hue(p2, q2, hk + 1.0 / 3) * 255), (byte)(Hue(p2, q2, hk) * 255), (byte)(Hue(p2, q2, hk - 1.0 / 3) * 255));
    }
}
