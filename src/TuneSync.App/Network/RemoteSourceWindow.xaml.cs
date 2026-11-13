using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace TuneSync.App.Network;

/// <summary>The "네트워크 위치 추가" wizard window (see <see cref="RemoteSourceSetupViewModel"/>).</summary>
public partial class RemoteSourceWindow : FluentWindow
{
    private readonly RemoteSourceSetupViewModel _viewModel;

    public RemoteSourceWindow(IReadOnlyList<TuneSync.Core.Models.RemoteSource>? saved = null,
        TuneSync.Core.Models.RemoteSource? start = null)
    {
        InitializeComponent();
        _viewModel = new RemoteSourceSetupViewModel(saved);
        DataContext = _viewModel;
        _viewModel.PasswordLoaded += (_, _) => PasswordBox.Password = _viewModel.Password;
        _viewModel.Finished += (_, _) =>
        {
            DialogResult = true;
            Close();
        };
        Loaded += (_, _) =>
        {
            // Opened for one saved location (the + menu): straight to its folders.
            var option = start is null ? null : _viewModel.SavedSources.FirstOrDefault(o => o.Source.Id == start.Id);
            if (option is not null) _viewModel.UseSavedCommand.Execute(option);
            else AddressBox.Focus();
        };
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
    public static TuneSync.Core.Models.LibraryFolderEntry? Run(Window? owner, NetworkLibrary network,
        TuneSync.Core.Models.RemoteSource? start = null)
    {
        var window = new RemoteSourceWindow(network.Sources, start) { Owner = owner };
        if (window.ShowDialog() != true || window.Result is not { } result) return null;
        network.Save(result.Source, result.Password);
        return result.Folder;
    }
}
