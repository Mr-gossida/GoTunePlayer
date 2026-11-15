using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using TuneSync.App.ViewModels;
using TuneSync.App.Views;
using TuneSync.Core.Models;
using TuneSync.Core.Services;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace TuneSync.App;

// The sleep timer button's menu and its "enter a time" dialog.
public partial class ShellWindow
{
    // --- 예약 종료 ---

    private static readonly (string Label, TimeSpan After)[] SleepPresets =
    [
        ("15분 뒤", TimeSpan.FromMinutes(15)),
        ("30분 뒤", TimeSpan.FromMinutes(30)),
        ("1시간 뒤", TimeSpan.FromHours(1)),
        ("1시간 30분 뒤", TimeSpan.FromMinutes(90)),
        ("2시간 뒤", TimeSpan.FromHours(2)),
    ];

    private void SleepTimer_Click(object sender, RoutedEventArgs e)
    {
        var timer = _services.GetRequiredService<SleepTimerViewModel>();
        var menu = new ContextMenu();

        if (timer.IsActive)
        {
            menu.Items.Add(new MenuItem
            {
                Header = timer.IsShuttingDown ? "곧 PC가 종료됩니다" : $"PC 종료까지 {timer.RemainingText} 남음",
                IsEnabled = false,
                Icon = new SymbolIcon { Symbol = SymbolRegular.Timer24 },
            });
            var cancel = new MenuItem { Header = "예약 종료 취소", Icon = new SymbolIcon { Symbol = SymbolRegular.Dismiss24 } };
            cancel.Click += (_, _) => timer.Cancel();
            menu.Items.Add(cancel);
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(new MenuItem { Header = timer.IsActive ? "다시 예약" : "음악을 멈추고 PC 종료", IsEnabled = false });
        foreach (var (label, after) in SleepPresets)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => timer.Start(after);
            menu.Items.Add(item);
        }
        var custom = new MenuItem { Header = "직접 입력…", Icon = new SymbolIcon { Symbol = SymbolRegular.Edit24 } };
        custom.Click += async (_, _) =>
        {
            if (await AskSleepTimeAsync() is { } after) timer.Start(after);
        };
        menu.Items.Add(custom);

        menu.PlacementTarget = SleepTimerButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    /// <summary>"직접 입력": a number and a unit (분 / 시간), up to 24 hours.</summary>
    private static async Task<TimeSpan?> AskSleepTimeAsync()
    {
        var amount = new Wpf.Ui.Controls.NumberBox
        {
            Value = 45,
            Minimum = 1,
            Maximum = 1440,
            MaxDecimalPlaces = 0,
            SmallChange = 5,
            Width = 140,
            ClearButtonEnabled = false,
        };
        var unit = new ComboBox { Width = 100, Margin = new Thickness(8, 0, 0, 0), ItemsSource = new[] { "분 뒤", "시간 뒤" }, SelectedIndex = 0 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        row.Children.Add(amount);
        row.Children.Add(unit);
        var content = new StackPanel();
        content.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "지정한 시간이 지나면 음악을 멈추고 PC를 종료합니다.\nPC가 꺼지기 전 1분 동안은 취소할 수 있습니다.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(row);

        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = "예약 종료 시간",
            Content = content,
            PrimaryButtonText = "예약",
            PrimaryButtonAppearance = ControlAppearance.Primary,
            CloseButtonText = "취소",
            IsSecondaryButtonEnabled = false,
        };
        Dialogs.PlaceOverApp(box);
        if (await box.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return null;

        var value = amount.Value ?? 0;
        var after = unit.SelectedIndex == 1 ? TimeSpan.FromHours(value) : TimeSpan.FromMinutes(value);
        if (after < TimeSpan.FromMinutes(1) || after > TimeSpan.FromHours(24))
        {
            Toast.Warning("예약할 수 없는 시간", "1분에서 24시간 사이로 입력해 주세요.");
            return null;
        }
        return after;
    }
}
