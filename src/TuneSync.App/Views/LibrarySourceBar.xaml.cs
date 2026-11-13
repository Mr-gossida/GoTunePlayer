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
        ListSavedSources(menu);
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    /// <summary>Network locations set up before, one click away (straight to picking a folder).</summary>
    private void ListSavedSources(ContextMenu menu)
    {
        foreach (var old in menu.Items.OfType<FrameworkElement>().Where(i => Equals(i.Tag, "saved")).ToList())
            menu.Items.Remove(old);
        if (Library is not { SavedSources.Count: > 0 } library) return;

        menu.Items.Add(new Separator { Tag = "saved" });
        menu.Items.Add(new MenuItem { Header = "저장된 네트워크 위치", IsEnabled = false, Tag = "saved" });
        foreach (var source in library.SavedSources)
        {
            var item = new MenuItem
            {
                Header = source.Name.Length > 0 ? source.Name : source.Address,
                InputGestureText = source.KindLabel,
                ToolTip = source.Address,
                Tag = "saved",
                Icon = new Wpf.Ui.Controls.SymbolIcon
                {
                    Symbol = Enum.Parse<Wpf.Ui.Controls.SymbolRegular>(Network.RemoteSourceSetupViewModel.IconFor(source.Kind)),
                },
            };
            item.Click += (_, _) => library.AddFromSavedSourceCommand.Execute(source);
            menu.Items.Add(item);
        }
    }

    private void AddLocal_Click(object sender, RoutedEventArgs e) => Library?.AddFoldersCommand.Execute(null);

    private void AddNetwork_Click(object sender, RoutedEventArgs e) => Library?.AddNetworkLocationCommand.Execute(null);

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (button.ContextMenu is not { } menu) return;
        var selected = Library?.QueueSelection.Count ?? 0;
        RemoveSelectedItem.Header = selected > 0 ? $"선택한 {selected}곡 목록에서 빼기" : "선택한 곡 목록에서 빼기";
        RemoveSelectedItem.IsEnabled = selected > 0;
        var excluded = Library?.ExcludedCount ?? 0;
        RestoreRemovedItem.Header = excluded > 0 ? $"뺀 곡 다시 넣기 ({excluded}곡)" : "뺀 곡 다시 넣기";
        RestoreRemovedItem.IsEnabled = excluded > 0;
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

    private async void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (Library is { QueueSelection.Count: > 0 } library) await library.RemoveFromListAsync(library.QueueSelection);
    }

    private void RestoreRemoved_Click(object sender, RoutedEventArgs e) => Library?.RestoreRemovedCommand.Execute(null);

    private void RemoveDuplicates_Click(object sender, RoutedEventArgs e) => Library?.RemoveDuplicateFolders();
}
