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
        return await box.ShowDialogAsync() == MessageBoxResult.Primary;
    }
}
