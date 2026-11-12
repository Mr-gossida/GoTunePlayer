using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace TuneSync.App.Network;

/// <summary>The "네트워크 위치 추가" wizard window (see <see cref="RemoteSourceSetupViewModel"/>).</summary>
public partial class RemoteSourceWindow : FluentWindow
{
    private readonly RemoteSourceSetupViewModel _viewModel = new();

    public RemoteSourceWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.Finished += (_, _) =>
        {
            DialogResult = true;
            Close();
        };
        Loaded += (_, _) => AddressBox.Focus();
        Closed += (_, _) => _viewModel.Dispose();
    }

    public (TuneSync.Core.Models.RemoteSource Source, string Password, TuneSync.Core.Models.LibraryFolderEntry Folder)? Result => _viewModel.Result;

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e) => _viewModel.Password = PasswordBox.Password;

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void FolderList_MouseDoubleClick(object sender, MouseButtonEventArgs e) =>
        _viewModel.OpenCommand.Execute(FolderList.SelectedItem as BrowseItem);

    private void FolderList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _viewModel.OpenCommand.Execute(FolderList.SelectedItem as BrowseItem);
        else if (e.Key == Key.Back) _viewModel.UpCommand.Execute(null);
        else return;
        e.Handled = true;
    }

    /// <summary>Runs the wizard; on success saves the location (password to Credential Manager)
    /// and returns the folder to add to the library.</summary>
    public static TuneSync.Core.Models.LibraryFolderEntry? Run(Window? owner, NetworkLibrary network)
    {
        var window = new RemoteSourceWindow { Owner = owner };
        if (window.ShowDialog() != true || window.Result is not { } result) return null;
        network.Save(result.Source, result.Password);
        return result.Folder;
    }
}
