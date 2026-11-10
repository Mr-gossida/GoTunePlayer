using System.Windows.Controls;
using TuneSync.App.ViewModels;

namespace TuneSync.App.Views;

/// <summary>
/// Interaction logic for RenameView.xaml
/// </summary>
public partial class RenameView : Page
{
    public RenameView(RenameViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
