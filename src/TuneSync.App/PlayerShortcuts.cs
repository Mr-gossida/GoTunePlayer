using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TuneSync.App.ViewModels;

namespace TuneSync.App;

/// <summary>Player keys shared by the full and mini players:
/// Space play/pause · ←/→ 5 seconds back/ahead · Ctrl+←/→ previous/next song · Ctrl+↑/↓ volume.
/// Ignored while typing in a text box (and arrows are left alone on a slider or list, where they
/// already mean something).</summary>
public static class PlayerShortcuts
{
    public static bool Handle(KeyEventArgs e, PlaybackViewModel playback)
    {
        var focused = Keyboard.FocusedElement;
        if (focused is TextBoxBase or PasswordBox) return false;
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        var none = Keyboard.Modifiers == ModifierKeys.None;
        var arrowsTaken = focused is Slider or ListBoxItem or ListBox or ComboBox or ComboBoxItem;

        switch (e.Key)
        {
            case Key.Space when none && focused is not ButtonBase:
                playback.TogglePlayPauseCommand.Execute(null);
                return true;
            case Key.Left when ctrl:
                playback.PlayPreviousCommand.Execute(null);
                return true;
            case Key.Right when ctrl:
                playback.PlayNextCommand.Execute(null);
                return true;
            case Key.Up when ctrl:
                playback.ChangeVolume(+0.05);
                return true;
            case Key.Down when ctrl:
                playback.ChangeVolume(-0.05);
                return true;
            case Key.Left when none && !arrowsTaken:
                playback.SeekBy(-5);
                return true;
            case Key.Right when none && !arrowsTaken:
                playback.SeekBy(+5);
                return true;
        }
        return false;
    }

    /// <summary>Called once at startup: a player button (one with the player's focus ring) clicked
    /// with the mouse hands keyboard focus back to its window. Left on the button, the focus made
    /// Space press that button again instead of play/pause (Next skipped another song), and the
    /// ring showed on it once a key was pressed or the window came back with Alt+Tab - an outline
    /// round a button nobody had moved to with the keyboard. Reached with Tab, a button keeps it.</summary>
    public static void ReleaseClickedButtons() =>
        EventManager.RegisterClassHandler(typeof(ButtonBase), ButtonBase.ClickEvent, new RoutedEventHandler(OnButtonClick), true);

    private static void OnButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ButtonBase button || !button.IsKeyboardFocused) return;
        if (InputManager.Current.MostRecentInputDevice is not (MouseDevice or StylusDevice)) return;
        if (!ReferenceEquals(button.FocusVisualStyle, Application.Current.TryFindResource("PlayerFocusVisual"))) return;
        // to the window (keys still reach it), or nowhere if it won't take it (not the active window)
        if (Window.GetWindow(button) is not { } window || !window.Focus()) Keyboard.ClearFocus();
    }
}
