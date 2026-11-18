using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using TuneSync.App.Views;
using TuneSync.Core.Models;

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
        _scrimRects = [];
        PlaceScrims();
    }

    // --- the shades (보통, 강하게): a soft dark rounded patch behind each group of words, the
    // buttons, the spectrum and the seek bar - what makes small white text hold over a busy, light
    // page. They are shapes of their own, under everything, so the spectrum and the seek bar,
    // redrawn many times a second, never carry a blur; each patch's blur is drawn once into a
    // cached bitmap and only redone when the patch moves or resizes (a new lyric line). ---

    private Rect[] _scrimRects = [];
    private bool _scrimsQueued;

    /// <summary>After any layout pass (a new line, a new song, another layout): the patches again,
    /// once the pass is over - and only changed when the groups actually moved.</summary>
    private void QueueScrims()
    {
        if (_scrimsQueued || !_settings.Hud.Clear || _settings.Hud.ClearShadow == HudShadow.Light) return;
        _scrimsQueued = true;
        Dispatcher.BeginInvoke(() => { _scrimsQueued = false; PlaceScrims(); }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void PlaceScrims()
    {
        var shadow = _settings.Hud.ClearShadow;
        if (!_settings.Hud.Clear || shadow == HudShadow.Light || !IsLoaded)
        {
            Scrims.Children.Clear();
            _scrimRects = [];
            return;
        }
        var strong = shadow == HudShadow.Strong;
        var pad = strong ? new Size(12, 6) : new Size(9, 4);
        var rects = Merge(ScrimAreas().Select(r => { r.Inflate(pad.Width, pad.Height); return r; }).ToList());
        if (rects.Length == _scrimRects.Length && rects.Zip(_scrimRects).All(p => Near(p.First, p.Second))) return;
        _scrimRects = rects;

        Scrims.Children.Clear();
        var blur = strong ? StrongBlur : NormalBlur;
        var fill = strong ? StrongFill : NormalFill;
        foreach (var r in rects)
        {
            // the blur sits on an inner border, so the outer one's cached bitmap holds it already
            // blurred; the margin leaves the blur room inside that bitmap
            var spread = blur.Radius;
            var patch = new Border
            {
                Width = r.Width + 2 * spread,
                Height = r.Height + 2 * spread,
                CacheMode = new BitmapCache(),
                Child = new Border
                {
                    Margin = new Thickness(spread),
                    CornerRadius = new CornerRadius(Math.Min(r.Height / 2, 16)),
                    Background = fill,
                    Effect = blur,
                },
            };
            Canvas.SetLeft(patch, r.X - spread);
            Canvas.SetTop(patch, r.Y - spread);
            Scrims.Children.Add(patch);
        }
    }

    private static readonly BlurEffect NormalBlur = Frozen(new BlurEffect { Radius = 10, RenderingBias = RenderingBias.Performance });
    private static readonly BlurEffect StrongBlur = Frozen(new BlurEffect { Radius = 14, RenderingBias = RenderingBias.Performance });
    private static readonly Brush NormalFill = Frozen(new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)));
    private static readonly Brush StrongFill = Frozen(new SolidColorBrush(Color.FromArgb(0xC4, 0, 0, 0)));

    /// <summary>Where the shades go, in the card's coordinates: around the words actually written
    /// in each group (not the whole width a line could take), its buttons, the spectrum and the
    /// seek bar of the layout showing.</summary>
    private IEnumerable<Rect> ScrimAreas()
    {
        var layout = Layouts.FirstOrDefault(l => l.IsVisible);
        if (layout is null) yield break;
        foreach (var group in TextPanels(layout).Where(g => g.IsVisible && g != LyricsButtons))
        {
            var area = Rect.Empty;
            foreach (var part in Visible(group))
                area.Union(part);
            if (!area.IsEmpty && area.Width > 0) yield return area;
        }
        // words on their own, outside any group of words
        foreach (var text in HaloTargets(layout).OfType<TextBlock>().Where(t => t.IsVisible && !TextPanels(layout).Any(g => g.IsAncestorOf(t))))
            if (Ink(text) is { IsEmpty: false } ink) yield return ink;
        foreach (var bar in Descendants(layout).Where(e => e is SpectrumView or ProgressBar && e.IsVisible))
            yield return Bounds(bar);
    }

    /// <summary>The written words and the buttons in a group.</summary>
    private IEnumerable<Rect> Visible(FrameworkElement group)
    {
        foreach (var e in Descendants(group).Prepend(group))
        {
            if (!e.IsVisible) continue;
            if (e is TextBlock text && Ink(text) is { IsEmpty: false } ink) yield return ink;
            else if (e is ButtonBase) yield return Bounds(e);
        }
    }

    /// <summary>Where a line's words are: as wide as the text itself (it may be shorter than the
    /// room it has), placed by its alignment.</summary>
    private Rect Ink(TextBlock text)
    {
        if (string.IsNullOrWhiteSpace(text.Text)) return Rect.Empty;
        var box = Bounds(text);
        // measured apart (measuring the TextBlock itself would start another layout pass)
        var written = new FormattedText(text.Text, System.Globalization.CultureInfo.CurrentUICulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.White,
            VisualTreeHelper.GetDpi(text).PixelsPerDip);
        var width = Math.Min(written.WidthIncludingTrailingWhitespace, box.Width);
        if (width <= 0) return Rect.Empty;
        var x = text.TextAlignment switch
        {
            TextAlignment.Center => box.X + (box.Width - width) / 2,
            TextAlignment.Right => box.Right - width,
            _ => box.X,
        };
        return new Rect(x, box.Y, width, box.Height);
    }

    private Rect Bounds(FrameworkElement e) => e.TransformToVisual(Scrims).TransformBounds(new Rect(e.RenderSize));

    private static IEnumerable<FrameworkElement> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<FrameworkElement>())
        {
            yield return child;
            if (child is ButtonBase or ContentControl) continue;
            foreach (var inner in Descendants(child)) yield return inner;
        }
    }

    /// <summary>Patches that touch become one (so where two meet isn't twice as dark).</summary>
    private static Rect[] Merge(List<Rect> rects)
    {
        for (var merged = true; merged;)
        {
            merged = false;
            for (var i = 0; i < rects.Count && !merged; i++)
                for (var j = i + 1; j < rects.Count; j++)
                {
                    if (!rects[i].IntersectsWith(rects[j])) continue;
                    var union = Rect.Union(rects[i], rects[j]);
                    rects[i] = union;
                    rects.RemoveAt(j);
                    merged = true;
                    break;
                }
        }
        return [.. rects.OrderBy(r => r.Y).ThenBy(r => r.X)];
    }

    private static bool Near(Rect a, Rect b) =>
        Math.Abs(a.X - b.X) < 1 && Math.Abs(a.Y - b.Y) < 1 && Math.Abs(a.Width - b.Width) < 1 && Math.Abs(a.Height - b.Height) < 1;

    /// <summary>Shows what a hotkey just switched, in the middle of the HUD for a moment.</summary>
    public void Flash(string text)
    {
        FlashText.Text = text;
        var show = new DoubleAnimationUsingKeyFrames();
        show.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.15))));
        show.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.4))));
        show.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.8))));
        FlashBadge.BeginAnimation(OpacityProperty, show);
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
