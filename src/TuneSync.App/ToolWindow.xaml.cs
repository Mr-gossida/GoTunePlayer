using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace TuneSync.App;

/// <summary>A separate window for one of the secondary tools (태그 관리, 폴더 합치기, 이름 변경,
/// 설정). The tool pages are DI singletons, so closing and reopening a tool brings back exactly
/// the state it was left in.</summary>
public partial class ToolWindow : FluentWindow
{
    public ToolWindow(Page page, string title, double width, double height)
    {
        InitializeComponent();
        Title = $"{title} - TuneSync";
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
