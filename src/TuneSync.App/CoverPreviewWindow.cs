using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace TuneSync.App;

/// <summary>A cover to show large: a caption, and how to get the full-size image (loaded when
/// the preview opens - list rows only keep small thumbnails).</summary>
public sealed record CoverPreviewItem(string Label, Func<Task<byte[]?>> Load);

/// <summary>Covers shown large over a dimmed window - side by side when comparing (current →
/// new). Click anywhere, Esc or ✕ closes it.</summary>
public sealed class CoverPreviewWindow : Window
{
    private CoverPreviewWindow(Window owner, IReadOnlyList<CoverPreviewItem> items)
    {
        Owner = owner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromArgb(0xD9, 0x08, 0x08, 0x0A));
        Title = "커버 크게 보기";

        // Cover the owner exactly (its normal or maximized bounds).
        var bounds = owner.WindowState == WindowState.Maximized
            ? SystemParameters.WorkArea
            : new Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight);
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;

        // Each cover gets an equal share of the width, and most of the height.
        var size = Math.Max(160, Math.Min(bounds.Height * 0.72, (bounds.Width * 0.86 - (items.Count - 1) * 72) / items.Count));
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
                row.Children.Add(new Wpf.Ui.Controls.SymbolIcon
                {
                    Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowRight24,
                    FontSize = 28,
                    Margin = new Thickness(22, 0, 22, 40),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
                });
            row.Children.Add(Panel(items[i], size, highlight: items.Count > 1 && i == items.Count - 1));
        }

        var close = new Wpf.Ui.Controls.Button
        {
            Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Dismiss24 },
            Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 16, 16, 0),
            Width = 40,
            Height = 40,
            Foreground = Brushes.White,
            ToolTip = "닫기 (Esc)",
        };
        close.Click += (_, _) => FadeOut();

        var hint = new TextBlock
        {
            Text = "아무 곳이나 클릭하거나 Esc를 누르면 닫힙니다",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 24),
        };

        Content = new Grid { Children = { row, close, hint } };
        Opacity = 0;
        Loaded += (_, _) => BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not Wpf.Ui.Controls.Button) FadeOut(); };
        KeyDown += (_, e) => { if (e.Key is Key.Escape or Key.Space or Key.Enter) FadeOut(); };
    }

    /// <summary>Opens the preview over <paramref name="owner"/> and waits until it's closed.</summary>
    public static void Open(Window? owner, params CoverPreviewItem[] items)
    {
        owner ??= Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;
        if (owner is null || items.Length == 0) return;
        new CoverPreviewWindow(owner, items).ShowDialog();
    }

    private static FrameworkElement Panel(CoverPreviewItem item, double size, bool highlight)
    {
        var image = new Image { Stretch = Stretch.Uniform, Width = size, Height = size };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var frame = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(highlight ? 2 : 0),
            BorderBrush = Application.Current.TryFindResource("TagChangeBrush") as Brush ?? Brushes.DeepSkyBlue,
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            Child = image,
            Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 8, Direction = 270, Opacity = 0.5 },
        };
        var spinner = new Wpf.Ui.Controls.ProgressRing { IsIndeterminate = true, Width = 36, Height = 36 };
        var holder = new Grid { Children = { frame, spinner } };

        var label = new TextBlock
        {
            Text = item.Label,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = highlight ? frame.BorderBrush : Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var info = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        };

        _ = LoadAsync();
        return new StackPanel { Children = { holder, label, info } };

        async Task LoadAsync()
        {
            byte[]? bytes = null;
            try { bytes = await item.Load(); } catch { /* shown as "불러오지 못함" */ }
            spinner.Visibility = Visibility.Collapsed;
            if (bytes is { Length: > 0 } && ImageHelper.ToBitmapImage(bytes) is BitmapSource bitmap)
            {
                image.Source = bitmap;
                var kind = TuneSync.Core.Services.CoverArtService.MimeTypeOf(bytes) == "image/png" ? "PNG" : "JPEG";
                info.Text = $"{bitmap.PixelWidth}×{bitmap.PixelHeight} · {kind} · {bytes.Length / 1024d:0} KB";
            }
            else
            {
                info.Text = bytes is null ? "커버 없음" : "이미지를 불러오지 못했습니다";
            }
        }
    }

    private bool _closing;

    private void FadeOut()
    {
        if (_closing) return;
        _closing = true;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }
}
