using System.Windows;
using System.Windows.Controls;
using TuneSync.App.ViewModels;

namespace TuneSync.App.Views;

public partial class LibrarySourceBar : UserControl
{
    public LibrarySourceBar()
    {
        InitializeComponent();
    }

    private LibraryViewModel? Library => DataContext as LibraryViewModel;

    /// <summary>"+": local folders/files or a network location.</summary>
    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (button.ContextMenu is not { } menu) return;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    private void AddLocal_Click(object sender, RoutedEventArgs e) => Library?.AddFoldersCommand.Execute(null);

    private void AddNetwork_Click(object sender, RoutedEventArgs e) => Library?.AddNetworkLocationCommand.Execute(null);

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (button.ContextMenu is not { } menu) return;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private async void RemoveAll_Click(object sender, RoutedEventArgs e)
    {
        if (Library is not { Folders.Count: > 0 } library) return;
        var confirmed = await Dialogs.ConfirmAsync("모두 제거",
            $"등록된 폴더/파일 {library.Folders.Count}개를 모두 목록에서 제거합니다.\n실제 파일은 삭제되지 않습니다.", "제거");
        if (confirmed) library.RemoveAllFolders();
    }

    private void RemoveMissing_Click(object sender, RoutedEventArgs e) => Library?.RemoveMissingFolders();

    private void RemoveDuplicates_Click(object sender, RoutedEventArgs e) => Library?.RemoveDuplicateFolders();
}
