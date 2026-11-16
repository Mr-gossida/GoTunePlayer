using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TuneSync.App.ViewModels;
using TuneSync.Core.Models;

namespace TuneSync.App.Views;

/// <summary>The key of a hotkey, set by pressing it: click (or tab to) the box, and the next key
/// pressed becomes the hotkey's key - with the modifiers held, when at least two are (one alone,
/// like Ctrl+S, would take a key every program uses). Esc leaves it as it was. While the box
/// waits, the hotkeys are let go, so the combination arrives here instead of acting.
/// DataContext: a <see cref="HotkeyRowViewModel"/>.</summary>
public sealed class HotkeyBox : ContentControl
{
    public static readonly DependencyProperty IsListeningProperty = DependencyProperty.Register(
        nameof(IsListening), typeof(bool), typeof(HotkeyBox), new PropertyMetadata(false));

    public static readonly DependencyProperty ServiceProperty = DependencyProperty.Register(
        nameof(Service), typeof(GlobalHotkeys), typeof(HotkeyBox));

    static HotkeyBox()
    {
        FocusableProperty.OverrideMetadata(typeof(HotkeyBox), new FrameworkPropertyMetadata(true));
        KeyboardNavigation.IsTabStopProperty.OverrideMetadata(typeof(HotkeyBox), new FrameworkPropertyMetadata(true));
    }

    public HotkeyBox()
    {
        Cursor = Cursors.Hand;
    }

    /// <summary>Waiting for a key.</summary>
    public bool IsListening { get => (bool)GetValue(IsListeningProperty); private set => SetValue(IsListeningProperty, value); }

    public GlobalHotkeys? Service { get => (GlobalHotkeys?)GetValue(ServiceProperty); set => SetValue(ServiceProperty, value); }

    private HotkeyRowViewModel? Row => DataContext as HotkeyRowViewModel;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (IsEnabled) Focus();
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (IsListening) return;
        IsListening = true;
        Service?.Suspend();
        Content = "키를 누르세요…";
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        StopListening();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (!IsListening) return;
        e.Handled = true;
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };
        var held = Held();
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            // only modifiers so far: show them as they build up
            var text = HotkeyText.Modifiers(held);
            Content = text.Length > 0 ? text + "+…" : "키를 누르세요…";
            return;
        }
        if (key == Key.Escape && held == HotkeyModifiers.None)
        {
            Leave();
            return;
        }
        if (key is Key.Tab && held == HotkeyModifiers.None)
        {
            e.Handled = false; // let Tab move on
            return;
        }
        var two = (held & (held - 1)) != 0; // at least two modifiers
        Row?.Set(two ? held : HotkeyModifiers.None, HotkeyText.Save(key));
        Leave();
    }

    private static HotkeyModifiers Held()
    {
        var modifiers = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= HotkeyModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= HotkeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= HotkeyModifiers.Shift;
        return modifiers;
    }

    /// <summary>Done: give the focus back to the page (which ends the listening).</summary>
    private void Leave()
    {
        StopListening();
        if (Parent is FrameworkElement || VisualParent is not null)
        {
            var scope = FocusManager.GetFocusScope(this);
            FocusManager.SetFocusedElement(scope, null);
            Keyboard.ClearFocus();
        }
    }

    private void StopListening()
    {
        if (!IsListening) return;
        IsListening = false;
        ClearValue(ContentProperty); // back to the binding's text (set in the style)
        Service?.Resume();
    }
}
