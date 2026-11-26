using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using TuneSync.App.ViewModels;
using TuneSync.App.Views;
using TuneSync.Core.Models;
using TuneSync.Core.Services.LyricsSync;
using Wpf.Ui.Controls;

namespace TuneSync.App;

/// <summary>"싱크 가사 일괄 만들기" (see <see cref="BatchSyncViewModel"/>). One at a time: offered
/// again while open, it's brought to the front instead.</summary>
public partial class BatchSyncWindow : FluentWindow
{
    private bool _closeConfirmed;

    internal BatchSyncWindow(BatchSyncViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        viewModel.CurrentChanged += item => Dispatcher.BeginInvoke(() => SongList.ScrollIntoView(item), DispatcherPriority.Loaded);
        SourceInitialized += (_, _) => PlaceOverMainWindow();
        Closed += (_, _) =>
        {
            viewModel.Dispose();
            if (ReferenceEquals(LyricsSyncActivity.Batch, this)) LyricsSyncActivity.Batch = null;
        };
    }

    public BatchSyncViewModel ViewModel { get; }

    /// <summary>"선택한 곡 싱크 가사 만들기": says which of <paramref name="selection"/> would be
    /// synced and how long that should take, warns that it's heavy work, and starts it once agreed.</summary>
    public static async Task OfferAsync(IReadOnlyList<LibraryFileViewModel> selection)
    {
        if (LyricsSyncActivity.Batch is { } open)
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            if (open.ViewModel.IsRunning)
                Toast.Info("싱크 가사 일괄 만들기가 진행 중입니다", "지금 작업이 끝나거나 중지하고 창을 닫은 뒤에 새로 시작할 수 있습니다.");
            else
                Toast.Info("싱크 가사 일괄 만들기 창이 열려 있습니다", "이 창을 닫은 뒤에 새로 시작할 수 있습니다.");
            return;
        }
        if (LyricsSyncActivity.SingleRunning)
        {
            Toast.Info("자동으로 맞추기가 진행 중입니다", "싱크 가사 만들기 창의 작업이 끝난 뒤에 시작할 수 있습니다.");
            return;
        }

        var plan = BatchSyncPlan.From(selection);
        if (plan.Plain.Count + plan.Synced.Count == 0)
        {
            Toast.Info("싱크 가사를 만들 곡이 없습니다", plan.NoLyrics > 0
                ? "선택한 곡에 가사가 없습니다. 먼저 '선택한 곡 가사 찾기'로 가사를 넣어 보세요."
                : "가사를 저장한 뒤에 다시 시도해 보세요.");
            return;
        }

        var services = ((App)Application.Current).Services;
        var settings = services.GetRequiredService<AppSettings>();
        var confirm = new BatchSyncConfirmViewModel(plan, settings, PowerRequest.OnBattery,
            needsModel: !WhisperModelStore.IsDownloaded || !VocalModelStore.IsDownloaded);
        var box = ConfirmBox(confirm);
        if (await box.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary || confirm.Count == 0) return;

        var window = new BatchSyncWindow(new BatchSyncViewModel(
            services.GetRequiredService<PlaybackViewModel>(),
            services.GetRequiredService<LibraryViewModel>(),
            services.GetRequiredService<Network.NetworkLibrary>(),
            settings,
            services.GetRequiredService<TagEditorViewModel>(),
            plan.Songs(confirm.IncludeSynced), confirm.IncludeSynced));
        LyricsSyncActivity.Batch = window;
        window.Show();
        window.ViewModel.Start();
    }

    /// <summary>The confirmation (the themed message box with the plan, the time and the warning
    /// inside); its start button follows the songs counted in.</summary>
    internal static Wpf.Ui.Controls.MessageBox ConfirmBox(BatchSyncConfirmViewModel confirm)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = "싱크 가사 일괄 만들기",
            Content = new BatchSyncConfirmView { DataContext = confirm },
            PrimaryButtonText = confirm.StartText,
            PrimaryButtonAppearance = ControlAppearance.Primary,
            PrimaryButtonIcon = new SymbolIcon(SymbolRegular.Play24),
            IsPrimaryButtonEnabled = confirm.Count > 0,
            IsSecondaryButtonEnabled = false,
            CloseButtonText = "취소",
            Width = 540,
            MaxWidth = 560,
        };
        confirm.PropertyChanged += (_, _) =>
        {
            box.PrimaryButtonText = confirm.StartText;
            box.IsPrimaryButtonEnabled = confirm.Count > 0;
        };
        Dialogs.PlaceOverApp(box);
        return box;
    }

    /// <summary>Over the middle of the player's window (not owned by it: it can be put behind).</summary>
    private void PlaceOverMainWindow()
    {
        var main = Application.Current.MainWindow;
        if (main is null || ReferenceEquals(main, this) || !main.IsVisible || main.WindowState == WindowState.Minimized)
        {
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Top + (area.Height - Height) / 2;
            return;
        }
        var bounds = main.WindowState == WindowState.Maximized ? SystemParameters.WorkArea : new Rect(main.Left, main.Top, main.ActualWidth, main.ActualHeight);
        Left = bounds.Left + (bounds.Width - Width) / 2;
        Top = Math.Max(bounds.Top, bounds.Top + (bounds.Height - Height) / 2);
    }

    private async void Review_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BatchSyncItemViewModel item }) await ViewModel.ReviewAsync(item, this);
    }

    private async void UndoOne_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BatchSyncItemViewModel item }) await ViewModel.UndoOneAsync(item);
    }

    private async void SaveOne_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BatchSyncItemViewModel item }) await ViewModel.SaveOneAsync(item);
    }

    /// <summary>A click on a row (not on its buttons or check box, which handle their own) picks it;
    /// Shift picks the songs between too.</summary>
    private void SongRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBoxItem { DataContext: BatchSyncItemViewModel item })
            ViewModel.Pick(item, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control && ViewModel.CanPick)
        {
            e.Handled = true;
            ViewModel.SelectAllCommand.Execute(null);
            return;
        }
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        // Esc lets the picked songs go first; then it closes
        if (ViewModel.HasSelection) ViewModel.ClearSelection();
        else Close();
    }

    /// <summary>Still working: ask before stopping it (what's done is saved either way).</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closeConfirmed && ViewModel.IsRunning && !Dispatcher.HasShutdownStarted)
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(async () =>
            {
                if (!await Dialogs.ConfirmAsync("싱크 가사 일괄 만들기",
                        KeepWords.Joined("작업을 중지하고 창을 닫을까요? 끝난 곡은 이미 저장되어 있습니다."), "중지하고 닫기")) return;
                _closeConfirmed = true;
                Close();
            });
            return;
        }
        base.OnClosing(e);
    }

    /// <summary>The icon in the progress card: a download while the models come, else a note.</summary>
    public static readonly IValueConverter HeaderIcon = new BoolToSymbol(SymbolRegular.ArrowDownload24, SymbolRegular.MusicNote224);

    private sealed class BoolToSymbol(SymbolRegular whenTrue, SymbolRegular whenFalse) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? whenTrue : whenFalse;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
