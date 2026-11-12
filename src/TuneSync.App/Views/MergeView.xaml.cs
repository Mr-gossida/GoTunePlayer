using System.IO;
using System.Windows;
using System.Windows.Controls;
using TuneSync.App.ViewModels;

namespace TuneSync.App.Views;

public partial class MergeView : Page
{
    private readonly MergeViewModel _vm;

    public MergeView(MergeViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _vm = viewModel;
        Loaded += (_, _) => _vm.Activate();
    }

    private static string? DroppedFolder(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths && Directory.Exists(paths[0]) ? paths[0] : null;

    // A folder dragged over a card lights it up; anything else is refused.
    private void Card_DragOver(object sender, DragEventArgs e)
    {
        var ok = DroppedFolder(e) is not null && _vm.IsIdle;
        e.Effects = ok ? DragDropEffects.Link : DragDropEffects.None;
        if (sender is Border card)
            card.BorderBrush = ok ? (System.Windows.Media.Brush)FindResource("TagChangeBrush") : (System.Windows.Media.Brush)FindResource("CardStrokeColorDefaultBrush");
        e.Handled = true;
    }

    private void Card_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border card) card.ClearValue(Border.BorderBrushProperty);
    }

    private void SourceCard_Drop(object sender, DragEventArgs e) => DropOn(sender, e, asTarget: false);

    private void TargetCard_Drop(object sender, DragEventArgs e) => DropOn(sender, e, asTarget: true);

    private void DropOn(object sender, DragEventArgs e, bool asTarget)
    {
        if (sender is Border card) card.ClearValue(Border.BorderBrushProperty);
        if (DroppedFolder(e) is { } folder) _vm.DropFolder(folder, asTarget);
    }
}
