using System.Windows.Controls;
using TuneSync.App.ViewModels;

namespace TuneSync.App.Views;

/// <summary>
/// Interaction logic for LibraryView.xaml
/// </summary>
public partial class LibraryView : Page
{
    public LibraryView(LibraryViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
