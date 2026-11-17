using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace TuneSync.App;

// Mouse pass-through (설정 › HUD 마우스 통과), and its "buttons can still be pressed" option.
public partial class HudWindow
{
    private const int VkLButton = 0x01;

    private DispatcherTimer? _pointerTimer;
    private bool _pointerOverCard, _takesMouse;

    /// <summary>Pass-through on or off. A transparent window style makes Windows hand every click
    /// (and hover) to the window below, so the card can't be grabbed until it is switched off again
    /// from 설정 or the tray menu. With the buttons option, the style is lifted only while the
    /// pointer is over one of the playback buttons (<see cref="TrackPointer"/>).</summary>
    public void ApplyClickThrough()
    {
        if (new WindowInteropHelper(this).Handle == IntPtr.Zero) return; // applied once the window exists
        var through = _settings.Hud.ClickThrough;
        SetPassThrough(through);
        _takesMouse = false;
        if (through)
        {
            // it stops getting the mouse now: drop the hover look and the menu
            Menu.IsOpen = false;
            if (IsVisible) Settle();
        }
        _pointerOverCard = false;
        UpdatePointerTracking();
    }

    private bool ButtonsReachable => _settings.Hud.ClickThrough && _settings.Hud.ClickThroughButtons;

    /// <summary>Windows can't pass clicks through only part of another program's window, so the
    /// pointer is followed instead (25 times a second, only while this option is on and the HUD
    /// shows): over a button the HUD takes the mouse, anywhere else it lets it through.</summary>
    private void UpdatePointerTracking()
    {
        if (ButtonsReachable && IsVisible)
        {
            _pointerTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Input, (_, _) => TrackPointer(), Dispatcher);
            _pointerTimer.Start();
            return;
        }
        _pointerTimer?.Stop();
        if (_takesMouse && _settings.Hud.ClickThrough) SetPassThrough(true);
        _takesMouse = false;
        if (_pointerOverCard && IsVisible) Settle();
        _pointerOverCard = false;
    }

    private void TrackPointer()
    {
        if (!IsVisible || PresentationSource.FromVisual(this) is null || !GetCursorPos(out var cursor)) return;
        var point = PointFromScreen(new Point(cursor.X, cursor.Y));
        var overCard = new Rect(Card.TranslatePoint(new Point(), this), Card.RenderSize).Contains(point);

        // the card brightens while the pointer is over it, so its buttons can be seen (and the
        // 가사 집중 layout's buttons come up); the menu and close buttons stay away
        if (overCard != _pointerOverCard)
        {
            _pointerOverCard = overCard;
            Fade(overCard ? 1 : Idle, overCard ? 0.15 : 0.4);
            LyricsButtons.BeginAnimation(OpacityProperty, new DoubleAnimation(overCard ? 1 : 0, TimeSpan.FromSeconds(overCard ? 0.15 : 0.3)));
        }

        // not switched mid-press: a button keeps the press it got until it is let go
        var overButton = overCard && ButtonAt(point);
        if (overButton != _takesMouse && (GetAsyncKeyState(VkLButton) & 0x8000) == 0)
        {
            _takesMouse = overButton;
            SetPassThrough(!overButton);
        }
    }

    /// <summary>A playback button of the current layout under the point (window coordinates).</summary>
    private bool ButtonAt(Point point)
    {
        var hit = false;
        VisualTreeHelper.HitTest(Card,
            target => target is UIElement { IsVisible: false } ? HitTestFilterBehavior.ContinueSkipSelfAndChildren
                : target == Chrome ? HitTestFilterBehavior.ContinueSkipSelfAndChildren // can't be seen in this mode
                : HitTestFilterBehavior.Continue,
            result =>
            {
                for (DependencyObject? e = result.VisualHit; e is not null && e != Card; e = VisualTreeHelper.GetParent(e))
                    if (e is ButtonBase { IsEnabled: true }) { hit = true; return HitTestResultBehavior.Stop; }
                return HitTestResultBehavior.Continue;
            },
            new PointHitTestParameters(TranslatePoint(point, Card)));
        return hit;
    }

    private void SetPassThrough(bool through)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var style = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, through ? style | WsExTransparent : style & ~WsExTransparent);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}
