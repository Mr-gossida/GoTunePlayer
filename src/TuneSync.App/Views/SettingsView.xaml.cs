using System.Windows.Controls;
using TuneSync.App.ViewModels;

namespace TuneSync.App.Views;

/// <summary>
/// Interaction logic for SettingsView.xaml
/// </summary>
public partial class SettingsView : Page
{
    public SettingsView(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        // Locations can be added from the player too: show the current list and cache use.
        Loaded += (_, _) => viewModel.RefreshNetwork();
    }
}
