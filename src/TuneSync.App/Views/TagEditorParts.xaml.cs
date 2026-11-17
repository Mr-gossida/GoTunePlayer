using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TuneSync.App.ViewModels;

namespace TuneSync.App.Views;

/// <summary>Handlers for the shared tag editor parts (TagEditorParts.xaml).</summary>
public partial class TagEditorParts : ResourceDictionary
{
    public TagEditorParts() => InitializeComponent();

    /// <summary>Esc in a changed (or invalid) field puts it back.</summary>
    private void Field_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || sender is not FrameworkElement { DataContext: TagFieldViewModel field }) return;
        if (!field.IsChanged && !field.HasError) return;
        field.RevertCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>A field's box: takes the caret when its field asks (ticking 적용 on a field whose
    /// songs differ, with no value typed yet).</summary>
    private void FieldBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TagFieldViewModel field } box || box.Tag is "hooked") return;
        box.Tag = "hooked";
        field.FocusRequested += () =>
        {
            if (!box.IsVisible) return;
            box.Focus();
            Keyboard.Focus(box is Wpf.Ui.Controls.AutoSuggestBox suggest ? FindChild<TextBox>(suggest) ?? (IInputElement)box : box);
        };
    }

    private void CoverCandidate_Loaded(object sender, RoutedEventArgs e) => CoverThumbnails.CheckSoon((FrameworkElement)sender);

    private void CoverCandidate_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => CoverThumbnails.CheckSoon((FrameworkElement)sender);

    internal static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }
}

/// <summary>커버 찾기 gallery: a tile's thumbnail is downloaded only once the tile is (nearly) on
/// screen in the scroll view around it.</summary>
internal static class CoverThumbnails
{
    /// <summary>After layout (so the tile has its position): fetch the thumbnail if it's in view.</summary>
    public static void CheckSoon(FrameworkElement tile) =>
        tile.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => Check(tile));

    /// <summary>Every tile of a gallery (its scroll view moved).</summary>
    public static void CheckAll(ItemsControl gallery)
    {
        foreach (var item in gallery.Items)
            if (gallery.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement container)
                Check(container);
    }

    private static void Check(FrameworkElement tile)
    {
        if (!tile.IsVisible || tile.DataContext is not CoverCandidateViewModel item) return;
        var scroller = Ancestor<ScrollViewer>(tile);
        if (scroller is null)
        {
            item.EnsureThumbnail();
            return;
        }
        var view = new Rect(0, 0, scroller.ViewportWidth, scroller.ViewportHeight);
        view.Inflate(0, 120); // a row ahead, so scrolling finds them ready
        var bounds = tile.TransformToAncestor(scroller).TransformBounds(new Rect(tile.RenderSize));
        if (bounds.IntersectsWith(view)) item.EnsureThumbnail();
    }

    private static T? Ancestor<T>(DependencyObject? d) where T : DependencyObject
    {
        for (d = d is null ? null : VisualTreeHelper.GetParent(d); d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is T found) return found;
        return null;
    }
}
