using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace TuneSync.App;

/// <summary>A separate window for one of the secondary tools (태그 관리, 폴더 합치기, 이름 변경,
/// 설정). The tool pages are DI singletons, so closing and reopening a tool brings back exactly
/// the state it was left in.</summary>
public partial class ToolWindow : FluentWindow
{
    /// <param name="playerColors">Take the player's colors (the cover's tint, a step apart)
    /// instead of the plain system backdrop.</param>
    public ToolWindow(Page page, string title, double width, double height, bool playerColors = false)
    {
        InitializeComponent();
        if (playerColors)
        {
            WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;
            PlayerTint.Visibility = System.Windows.Visibility.Visible;
            PlayerVeil.Visibility = System.Windows.Visibility.Visible;
        }
        Title = $"{title} - GoTune";
        ToolTitleBar.Title = title;
        Width = width;
        Height = height;
        MinWidth = Math.Min(width, 760);
        MinHeight = Math.Min(height, 520);

        var snackbar = new Wpf.Ui.SnackbarService();
        snackbar.SetSnackbarPresenter(SnackbarPresenter);
        Toast.Register(this, snackbar);

        ContentFrame.Navigate(page);

        // Detach the page so the same instance can be hosted again next time the tool opens.
        Closed += (_, _) => ContentFrame.Content = null;
    }
}
