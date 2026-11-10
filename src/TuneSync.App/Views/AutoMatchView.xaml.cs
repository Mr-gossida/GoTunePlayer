using System.Windows.Controls;
using TuneSync.App.ViewModels;

namespace TuneSync.App.Views;

/// <summary>
/// Interaction logic for AutoMatchView.xaml
/// </summary>
public partial class AutoMatchView : Page
{
    public AutoMatchView(AutoMatchViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
