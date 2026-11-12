using Wpf.Ui.Controls;

namespace TuneSync.App;

/// <summary>Theme-matched yes/no confirmation before destructive actions (the stock
/// System.Windows.MessageBox ignores the app's theme). Errors and notices use <see cref="Toast"/>.</summary>
public static class Dialogs
{
    public static async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var box = new MessageBox
        {
            Title = title,
            Content = message,
            PrimaryButtonText = confirmText,
            PrimaryButtonAppearance = ControlAppearance.Primary,
            CloseButtonText = "취소",
            IsPrimaryButtonEnabled = true,
            IsSecondaryButtonEnabled = false,
        };
        FitButtons(box, confirmText, "취소");
        return await box.ShowDialogAsync() == MessageBoxResult.Primary;
    }

    /// <summary>The dialog splits its width evenly between its buttons, so a long label
    /// ("저장하지 않고 닫기") was cut off: widen the dialog until every label fits.</summary>
    private static void FitButtons(MessageBox box, params string[] labels)
    {
        var typeface = new System.Windows.Media.Typeface(new System.Windows.Media.FontFamily("Segoe UI Variable, Malgun Gothic"),
            System.Windows.FontStyles.Normal, System.Windows.FontWeights.Normal, System.Windows.FontStretches.Normal);
        var widest = labels.Max(label => new System.Windows.Media.FormattedText(label, System.Globalization.CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight, typeface, 14, System.Windows.Media.Brushes.Black, 1.0).WidthIncludingTrailingWhitespace);
        // button padding + spacing between buttons + the dialog's own margins
        var needed = labels.Length * (widest + 56) + (labels.Length - 1) * 8 + 48;
        box.MinWidth = Math.Max(box.MinWidth, needed);
        if (double.IsNaN(box.Width) || box.Width < needed) box.Width = Math.Max(400, needed);
    }

    /// <summary>Adding folders/files to a non-empty list: add to it, or start over with only the
    /// new selection? "다음부터 묻지 않기" saves the answer (changeable in Settings). Null when
    /// cancelled.</summary>
    public static async Task<(TuneSync.Core.Models.LibraryAddMode Mode, bool Remember)?> ChooseAddModeAsync(int existingCount, int newCount)
    {
        var remember = new System.Windows.Controls.CheckBox
        {
            Content = "다음부터 묻지 않기",
            Margin = new System.Windows.Thickness(0, 16, 0, 0),
        };
        var content = new System.Windows.Controls.StackPanel();
        content.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = $"선택한 {newCount}개 항목을 어떻게 등록할까요?\n\n"
                 + $"· 기존 목록에 추가: 지금 등록된 {existingCount}개 폴더/파일은 그대로 두고 함께 재생합니다.\n"
                 + "· 새로 등록: 기존 목록을 비우고 선택한 항목으로만 새로 만듭니다. (실제 파일은 삭제되지 않습니다)",
            TextWrapping = System.Windows.TextWrapping.Wrap,
        });
        content.Children.Add(remember);

        var box = new MessageBox
        {
            Title = "폴더/파일 추가",
            Content = content,
            PrimaryButtonText = "기존 목록에 추가",
            PrimaryButtonAppearance = ControlAppearance.Primary,
            SecondaryButtonText = "새로 등록",
            IsSecondaryButtonEnabled = true,
            CloseButtonText = "취소",
            MaxWidth = 560,
        };
        FitButtons(box, "기존 목록에 추가", "새로 등록", "취소");
        var mode = await box.ShowDialogAsync() switch
        {
            MessageBoxResult.Primary => TuneSync.Core.Models.LibraryAddMode.Append,
            MessageBoxResult.Secondary => TuneSync.Core.Models.LibraryAddMode.Replace,
            _ => (TuneSync.Core.Models.LibraryAddMode?)null,
        };
        return mode is { } m ? (m, remember.IsChecked == true) : null;
    }
}
