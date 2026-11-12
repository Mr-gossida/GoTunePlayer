using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using TuneSync.App.ViewModels;

namespace TuneSync.App;

/// <summary>The "싱크 가사 만들기" editor (see <see cref="LyricsSyncViewModel"/>).</summary>
public partial class LyricsSyncWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly LyricsSyncViewModel _viewModel;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private bool _closeConfirmed;

    private LyricsSyncWindow(LyricsSyncViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.ScrollRequested += line => Dispatcher.BeginInvoke(() => LinesList.ScrollIntoView(line), DispatcherPriority.Loaded);
        viewModel.Finished += saved =>
        {
            _closeConfirmed = true;
            DialogResult = saved;
        };
        _tick.Tick += (_, _) =>
        {
            viewModel.Tick();
            var pos = viewModel.IsThisSongLoaded ? viewModel.Playback.CurrentPosition : TimeSpan.Zero;
            PositionText.Text = $"{(int)pos.TotalMinutes:00}:{pos.Seconds:00}.{pos.Milliseconds / 10:00}";
        };
        Loaded += (_, _) => _tick.Start();
        Closed += (_, _) =>
        {
            _tick.Stop();
            viewModel.Dispose();
        };
    }

    /// <summary>Opens the editor for <paramref name="file"/>'s plain lyrics. True when synced
    /// lyrics were saved.</summary>
    public static bool Run(Window? owner, LibraryFileViewModel file, string plainLyrics)
    {
        var window = Create(file, plainLyrics);
        window.Owner = owner;
        return window.ShowDialog() == true;
    }

    internal static LyricsSyncWindow Create(LibraryFileViewModel file, string plainLyrics)
    {
        var services = ((App)Application.Current).Services;
        var viewModel = new LyricsSyncViewModel(
            services.GetRequiredService<PlaybackViewModel>(),
            services.GetRequiredService<LibraryViewModel>(),
            services.GetRequiredService<Network.NetworkLibrary>(),
            file, plainLyrics);
        return new LyricsSyncWindow(viewModel);
    }

    internal LyricsSyncViewModel ViewModel => _viewModel;

    private void TimeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SyncLineViewModel line }) _viewModel.PlayFromCommand.Execute(line);
    }

    /// <summary>The editor's keys work wherever the focus is (the list, a button, …).</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Space:
                _viewModel.StampCommand.Execute(null);
                break;
            case Key.Enter:
                _viewModel.PlayFromCommand.Execute(null);
                break;
            case Key.Left:
                _viewModel.Nudge(shift ? -0.5 : -0.1);
                break;
            case Key.Right:
                _viewModel.Nudge(shift ? 0.5 : 0.1);
                break;
            case Key.Up or Key.Down:
                Move(e.Key == Key.Up ? -1 : 1);
                break;
            case Key.Delete or Key.Back:
                _viewModel.ClearTimeCommand.Execute(null);
                break;
            case Key.S when Keyboard.Modifiers == ModifierKeys.Control:
                _viewModel.SaveCommand.Execute(null);
                break;
            case Key.Escape:
                Close();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void Move(int by)
    {
        var lines = _viewModel.Lines;
        if (lines.Count == 0) return;
        var index = _viewModel.SelectedLine is { } s ? lines.IndexOf(s) : -1;
        var next = Math.Clamp(index + by, 0, lines.Count - 1);
        _viewModel.SelectedLine = lines[next];
        LinesList.ScrollIntoView(lines[next]);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Unsaved timing: ask before throwing it away.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closeConfirmed && (_viewModel.HasChanges || _viewModel.IsBusy))
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(async () =>
            {
                if (!await Dialogs.ConfirmAsync("싱크 가사 만들기", "맞춘 시간을 저장하지 않고 닫을까요?", "저장하지 않고 닫기")) return;
                _closeConfirmed = true;
                _viewModel.CancelWorkCommand.Execute(null);
                Close();
            });
            return;
        }
        base.OnClosing(e);
    }
}
