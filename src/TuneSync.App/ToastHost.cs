using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace TuneSync.App;

public enum ToastKind { Info, Success, Warning, Error }

/// <summary>Where a window shows its toasts (<see cref="Toast"/>): small capsules centered at the
/// bottom, newest lowest. Each one says what happened with a colored icon (the kind) on a quiet
/// neutral surface, and a thin line in the player's accent color shrinks away as its time runs
/// out. Hovering a capsule holds it; clicking it dismisses it. The host itself lets clicks
/// through, so only the capsules take the mouse.</summary>
public sealed class ToastHost : StackPanel
{
    private const int MaxShown = 3;

    public ToastHost()
    {
        VerticalAlignment = VerticalAlignment.Bottom;
        HorizontalAlignment = HorizontalAlignment.Center;
    }

    public void Show(ToastKind kind, string title, string message, TimeSpan duration)
    {
        // The same notice again (a retry failing twice) restarts the one already showing.
        foreach (var existing in Children.OfType<Capsule>())
            if (!existing.Leaving && existing.Title == title && existing.Message == message)
            {
                existing.Restart(duration);
                return;
            }

        var capsule = new Capsule(this, kind, title, message, duration);
        Children.Add(capsule);
        var live = Children.OfType<Capsule>().Where(c => !c.Leaving).ToList();
        for (var i = 0; i < live.Count - MaxShown; i++) live[i].Dismiss();
    }

    private sealed class Capsule : Border
    {
        private readonly ToastHost _host;
        private readonly ScaleTransform _remaining = new(1, 1);
        private readonly TranslateTransform _slide = new(0, 14);
        private AnimationClock? _timer;

        public string Title { get; }
        public string Message { get; }
        public bool Leaving { get; private set; }

        public Capsule(ToastHost host, ToastKind kind, string title, string message, TimeSpan duration)
        {
            _host = host;
            Title = title;
            Message = message;
            var dark = ApplicationThemeManager.GetAppTheme() != ApplicationTheme.Light;

            MaxWidth = 520;
            Margin = new Thickness(0, 6, 0, 0);
            HorizontalAlignment = HorizontalAlignment.Center;
            CornerRadius = new CornerRadius(22);
            Padding = new Thickness(10, 9, 18, 10);
            Background = new SolidColorBrush(dark ? Color.FromArgb(0xF2, 0x24, 0x22, 0x21) : Color.FromArgb(0xFA, 0xFB, 0xFA, 0xF9));
            BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0, 0, 0));
            BorderThickness = new Thickness(1);
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 22, ShadowDepth = 6, Direction = 270,
                Opacity = dark ? 0.45 : 0.16, Color = Colors.Black,
            };
            RenderTransform = _slide;
            Opacity = 0;
            Cursor = Cursors.Hand;
            ToolTip = "클릭하면 닫힙니다";

            var (symbol, tint) = Look(kind, dark);
            var icon = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12),
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 10, 0),
                Background = new SolidColorBrush(Color.FromArgb(dark ? (byte)0x2E : (byte)0x1C, tint.R, tint.G, tint.B)),
                Child = new SymbolIcon { Symbol = symbol, Filled = true, FontSize = 15, Foreground = new SolidColorBrush(tint),
                                         HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(dark ? Color.FromRgb(0xF3, 0xEF, 0xEB) : Color.FromRgb(0x1C, 0x1A, 0x19)),
            });
            if (!string.IsNullOrWhiteSpace(message))
                text.Children.Add(new TextBlock
                {
                    Text = message, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0),
                    MaxHeight = 64, TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = new SolidColorBrush(dark ? Color.FromRgb(0xA9, 0xA1, 0x9A) : Color.FromRgb(0x66, 0x60, 0x5B)),
                });

            var row = new DockPanel();
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(text);

            // The time left: a hairline along the bottom edge, inset so the pill's curve doesn't clip it.
            var line = new Border
            {
                Height = 2, CornerRadius = new CornerRadius(1), Opacity = 0.75,
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6, 0, -4, -10),
                RenderTransform = _remaining,
            };
            line.SetResourceReference(BackgroundProperty, PlayerPalette.AccentKey);

            var body = new Grid();
            body.Children.Add(row);
            body.Children.Add(line);
            Child = body;

            AutomationProperties.SetName(this, string.IsNullOrWhiteSpace(message) ? title : $"{title}. {message}");
            AutomationProperties.SetLiveSetting(this, kind == ToastKind.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);

            Loaded += (_, _) =>
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
                _slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
                StartTimer(duration);
                UIElementAutomationPeer.CreatePeerForElement(this)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            };
            MouseEnter += (_, _) => _timer?.Controller?.Pause();
            MouseLeave += (_, _) => _timer?.Controller?.Resume();
            MouseLeftButtonUp += (_, _) => Dismiss();
        }

        private static (SymbolRegular, Color) Look(ToastKind kind, bool dark) => kind switch
        {
            // Windows' own status colors (Fluent "system fill"), as light/dark pairs.
            ToastKind.Success => (SymbolRegular.CheckmarkCircle24, dark ? Color.FromRgb(0x6C, 0xCB, 0x5F) : Color.FromRgb(0x0F, 0x7B, 0x0F)),
            ToastKind.Warning => (SymbolRegular.Warning24, dark ? Color.FromRgb(0xFC, 0xE1, 0x00) : Color.FromRgb(0x9D, 0x5D, 0x00)),
            ToastKind.Error => (SymbolRegular.ErrorCircle24, dark ? Color.FromRgb(0xFF, 0x99, 0xA4) : Color.FromRgb(0xC4, 0x2B, 0x1C)),
            _ => (SymbolRegular.Info24, dark ? Color.FromRgb(0x99, 0xC8, 0xFF) : Color.FromRgb(0x00, 0x5F, 0xB8)),
        };

        private void StartTimer(TimeSpan duration)
        {
            _timer?.Controller?.Stop();
            var shrink = new DoubleAnimation(1, 0, duration);
            var clock = shrink.CreateClock();
            clock.Completed += (_, _) => { if (clock == _timer) Dismiss(); }; // not a replaced one
            _timer = clock;
            _remaining.ApplyAnimationClock(ScaleTransform.ScaleXProperty, clock);
            if (IsMouseOver) clock.Controller?.Pause();
        }

        public void Restart(TimeSpan duration)
        {
            StartTimer(duration);
            // a small nudge, so the repeat is noticed
            _slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-4, 0, TimeSpan.FromMilliseconds(220))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        public void Dismiss()
        {
            if (Leaving) return;
            Leaving = true;
            IsHitTestVisible = false;
            _timer?.Controller?.Pause();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
            fade.Completed += (_, _) => _host.Children.Remove(this);
            _slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, TimeSpan.FromMilliseconds(180)));
            BeginAnimation(OpacityProperty, fade);
        }
    }
}
