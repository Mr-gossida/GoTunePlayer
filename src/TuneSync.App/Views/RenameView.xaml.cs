using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TuneSync.App.ViewModels;
using TuneSync.Core.Models;

namespace TuneSync.App.Views;

public partial class RenameView : Page
{
    private readonly RenameViewModel _vm;
    private TextBox? _lastPatternBox;

    public RenameView(RenameViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _vm = viewModel;
        Loaded += (_, _) => _vm.Activate();
        Unloaded += (_, _) => _vm.Deactivate();
    }

    private void PatternBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e) => _lastPatternBox = sender as TextBox;

    /// <summary>A token chip: put its token at the cursor of the pattern last edited (or of the
    /// one the mode uses), then keep typing there.</summary>
    private void Token_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RenameToken token) return;
        var box = _lastPatternBox is { IsVisible: true } last ? last
            : _vm.Mode == RenameMode.Album ? AlbumPatternBox : SinglePatternBox;

        var caret = Math.Clamp(box.CaretIndex, 0, box.Text.Length);
        var before = box.Text[..caret];
        var insert = token.Token;
        if (before.Length > 0 && !char.IsWhiteSpace(before[^1]) && before[^1] is not ('[' or '(')) insert = " " + insert;
        box.Text = before + insert + box.Text[caret..];
        box.Focus();
        box.CaretIndex = caret + insert.Length;
    }
}
