using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TuneSync.App.ViewModels;

namespace TuneSync.App;

/// <summary>Full-screen "now playing" view shown after a while without input during playback
/// (the picture is <see cref="NowPlayingStage"/>, one per monitor). Any mouse movement or key
/// brings the player back - or, with 'ESC only', just Esc.</summary>
public partial class ScreenSaverWindow : Window
{
    private readonly NowPlayingStage _stage;

    public ScreenSaverRole Role => _stage.Role;

    /// <summary>설정 › 화면 보호기 종료 방법 = ESC only, for the windows about to open.</summary>
    public static bool EscapeOnly { get; set; }

    private readonly bool _escapeOnly = EscapeOnly;
    private readonly DispatcherTimer _promptTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private Point? _firstMouse;
    private bool _closing;

    /// <summary>The monitor's exact bounds in device pixels: the window is placed with them once
    /// it has a handle (monitors can have different scaling, so DIPs alone can be off).</summary>
    public Int32Rect? PixelBounds { get; init; }

    public ScreenSaverWindow(PlaybackViewModel playback, Rect screen,
                             ScreenSaverRole role = ScreenSaverRole.Main, bool lyricsElsewhere = false)
    {
        InitializeComponent();
        _stage = new NowPlayingStage(playback, role, lyricsElsewhere, screen.Width);
        StageHost.Content = _stage;
        if (role != ScreenSaverRole.Main) Hint.Visibility = Visibility.Collapsed;
        if (_escapeOnly) Hint.Text = "ESC 키를 누르면 돌아갑니다";
        _promptTimer.Tick += (_, _) => HideExitPrompt();
        SourceInitialized += (_, _) =>
        {
            if (PixelBounds is not { } px) return;
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            SetWindowPos(handle, new IntPtr(-1), px.X, px.Y, px.Width, px.Height, 0x0010); // topmost, no activate
        };

        Left = screen.Left;
        Top = screen.Top;
        Width = screen.Width;
        Height = screen.Height;

        Loaded += (_, _) =>
        {
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(900)));
            Hint.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromSeconds(1.5)) { BeginTime = TimeSpan.FromSeconds(4) });
        };
        Closed += (_, _) =>
        {
            _promptTimer.Stop();
            StageHost.Content = null; // stops the stage's timers and light
        };
    }

    /// <summary>This screen's part of the light shared by all monitors.</summary>
    public void SetAuroraRegion(Point offset, Size span) => _stage.SetAuroraRegion(offset, span);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    // --- any real input ends it (a tiny mouse jitter doesn't) ---

    // With 'ESC only' (설정 › 화면 보호기 종료 방법), the same input shows how to leave instead.

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var position = e.GetPosition(this);
        if (_firstMouse is not { } first)
        {
            _firstMouse = position;
            return;
        }
        if (_escapeOnly && ExitPrompt.Visibility == Visibility.Visible)
        {
            _promptTimer.Stop(); // keeps showing while the mouse moves
            _promptTimer.Start();
            return;
        }
        if (Math.Abs(position.X - first.X) + Math.Abs(position.Y - first.Y) <= 12) return;
        _firstMouse = position;
        if (_escapeOnly) ShowExitPrompt();
        else Dismiss();
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (_escapeOnly)
        {
            // A click on the on-screen Esc button goes through to it.
            if (e.OriginalSource is DependencyObject source && IsInside(source, ExitButton)) return;
            e.Handled = true;
            ShowExitPrompt();
            return;
        }
        e.Handled = true;
        Dismiss();
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (_escapeOnly)
        {
            e.Handled = true;
            ShowExitPrompt();
        }
        else Dismiss();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        if (!_escapeOnly || e.Key == Key.Escape) Dismiss();
        else ShowExitPrompt();
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e) => Dismiss();

    private static bool IsInside(DependencyObject element, DependencyObject container)
    {
        for (var current = element; current is not null;
             current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current == container) return true;
        return false;
    }

    /// <summary>The 'press Esc' note with its button, and the mouse pointer to reach it; both go
    /// away again a few seconds after the input stops.</summary>
    private void ShowExitPrompt()
    {
        if (_closing) return;
        _promptTimer.Stop();
        _promptTimer.Start();
        Cursor = null; // the arrow back, to reach the button
        if (ExitPrompt.Visibility == Visibility.Visible && ExitPrompt.Opacity > 0.99) return;
        ExitPrompt.Visibility = Visibility.Visible;
        Hint.BeginAnimation(OpacityProperty, null); // the prompt takes the hint's place
        Hint.Opacity = 0;
        ExitPrompt.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(250)));
    }

    private void HideExitPrompt()
    {
        _promptTimer.Stop();
        if (ExitPrompt.IsMouseOver)
        {
            _promptTimer.Start(); // not while the pointer is on it
            return;
        }
        Cursor = Cursors.None;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(500));
        fade.Completed += (_, _) => { if (ExitPrompt.Opacity == 0) ExitPrompt.Visibility = Visibility.Collapsed; };
        ExitPrompt.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Fades out, then closes.</summary>
    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
        fade.Completed += (_, _) => Close();
        Root.BeginAnimation(OpacityProperty, fade);
    }
}
