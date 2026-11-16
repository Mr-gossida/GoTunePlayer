using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using TuneSync.App.ViewModels;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.App;

/// <summary>The HUD's window (see the XAML). Above every other window and never activated, so
/// using its buttons doesn't take the keyboard from the program being worked in; as solid as
/// set, solid while the mouse is over it; dragged anywhere and reopened where it was left.</summary>
public partial class HudWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    private readonly HudViewModel _hud;
    private readonly AppSettings _settings;
    private bool _hovered, _closing;

    public HudWindow(HudViewModel hud, AppSettings settings)
    {
        InitializeComponent();
        _hud = hud;
        _settings = settings;
        DataContext = hud.Playback;
        Tag = hud; // the menu and close button work on the HUD itself
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLong(handle, GwlExStyle, GetWindowLong(handle, GwlExStyle) | WsExNoActivate | WsExToolWindow);
        };
        Menu.Closed += (_, _) => { if (!IsMouseOver) Settle(); };
        Closing += (_, e) =>
        {
            // Alt+F4 on it: just hide (the app stays)
            if (_closing) return;
            e.Cancel = true;
            _hud.IsOpen = false;
        };
    }

    public void ShowHud()
    {
        var area = SystemParameters.WorkArea;
        var hud = _settings.Hud;
        var left = hud.Left ?? area.Right - Width - 16;
        var top = hud.Top ?? area.Top + 16;
        // back on screen if the monitor it was on is gone
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!screen.IntersectsWith(new Rect(left, top, Width, 60)))
        {
            left = area.Right - Width - 16;
            top = area.Top + 16;
        }
        Left = left;
        Top = top;
        Opacity = 0;
        Show();
        Topmost = false; // re-assert: put it above windows that went topmost meanwhile
        Topmost = true;
        Fade(Idle, 0.25);
    }

    public void HideHud()
    {
        Menu.IsOpen = false;
        var fade = new DoubleAnimation(0, TimeSpan.FromSeconds(0.18));
        fade.Completed += (_, _) => { if (!_hud.IsOpen) Hide(); };
        BeginAnimation(OpacityProperty, fade);
    }

    public void CloseForGood()
    {
        _closing = true;
        Close();
    }

    /// <summary>The set opacity changed (its menu, or 설정).</summary>
    public void ApplyOpacity()
    {
        if (!_hovered && IsVisible) Fade(Idle, 0.12);
    }

    private double Idle => Math.Clamp(_settings.Hud.Opacity, HudSettings.MinOpacity, 1);

    private void Fade(double to, double seconds) =>
        BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromSeconds(seconds)) { EasingFunction = new QuadraticEase() });

    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        _hovered = true;
        Fade(1, 0.15);
        Chrome.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromSeconds(0.15)));
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        if (Menu.IsOpen) return; // the menu is a window of its own: the pointer is in it
        Settle();
    }

    private void Settle()
    {
        _hovered = false;
        Fade(Idle, 0.4);
        Chrome.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromSeconds(0.3)));
    }

    /// <summary>Dragged from anywhere but its buttons; where it is left is kept.</summary>
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindButton(source)) return;
        try { DragMove(); } catch (InvalidOperationException) { return; }
        _settings.Hud.Left = Left;
        _settings.Hud.Top = Top;
        AppSettingsStore.Save(_settings);
    }

    private static bool FindButton(DependencyObject? element)
    {
        for (; element is not null; element = element is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                 ? System.Windows.Media.VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            if (element is ButtonBase) return true;
        return false;
    }

    private void Menu_Click(object sender, RoutedEventArgs e) => Menu.IsOpen = !Menu.IsOpen;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
}
