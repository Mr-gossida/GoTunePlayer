using System.Windows.Controls;
using TuneSync.App.ViewModels;

namespace TuneSync.App.Views;

/// <summary>
/// Interaction logic for MergeView.xaml
/// </summary>
public partial class MergeView : Page
{
    public MergeView(MergeViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
