using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TuneSync.App.ViewModels;

namespace TuneSync.App.Views;

/// <summary>Settings in categories (the list on the left) with a search over all of them. The
/// page takes the player's colors: its accent for switches, the selection mark and highlights
/// (the window's background follows the cover too - see ToolWindow).</summary>
public partial class SettingsView : Page
{
    public SettingsView(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        // Locations can be added from the player too: show the current list and cache use.
        Loaded += (_, _) =>
        {
            viewModel.RefreshNetwork();
            UseAccent();
            PlayerPalette.Changed += UseAccent;
        };
        Unloaded += (_, _) => PlayerPalette.Changed -= UseAccent;
        ShowCategory("General");
    }

    /// <summary>The Windows accent the controls use, swapped for the player's here (a toggle in
    /// the cover's color, not the system blue).</summary>
    private void UseAccent()
    {
        if (Application.Current.TryFindResource(PlayerPalette.AccentKey) is not Brush accent ||
            Application.Current.TryFindResource(PlayerPalette.OnAccentKey) is not Brush onAccent) return;
        foreach (var key in new[] { "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush",
                                    "AccentTextFillColorPrimaryBrush", "AccentTextFillColorSecondaryBrush", "SystemAccentColorPrimaryBrush" })
            Resources[key] = accent;
        Resources["TextOnAccentFillColorPrimaryBrush"] = onAccent;
        Resources["TextOnAccentFillColorSecondaryBrush"] = onAccent;
    }

    private void OpenEqualizer_Click(object sender, RoutedEventArgs e) => (Application.Current.MainWindow as ShellWindow)?.OpenEqualizer();

    private IEnumerable<StackPanel> CategoryPanels => Categories.Children.OfType<StackPanel>().Where(p => p.Tag is string);

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // (Also fires while the page is still being built, before the content exists.)
        if (Categories is null || NavList.SelectedItem is not ListBoxItem { Tag: string category }) return;
        if (SearchBox.Text.Length > 0) SearchBox.Text = ""; // picking a category ends the search
        ShowCategory(category);
    }

    private void ShowCategory(string category)
    {
        foreach (var panel in CategoryPanels)
        {
            panel.Visibility = Equals(panel.Tag, category) ? Visibility.Visible : Visibility.Collapsed;
            foreach (UIElement child in panel.Children) child.Visibility = Visibility.Visible;
        }
        NoMatch.Visibility = Visibility.Collapsed;
        ContentScroller.ScrollToTop();
    }

    // --- search: every category's cards whose title, keywords or explanation have all the words ---

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (Categories is null) return;
        var query = SearchBox.Text.Trim();
        if (query.Length == 0)
        {
            ShowCategory(NavList.SelectedItem is ListBoxItem { Tag: string c } ? c : "General");
            return;
        }
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var group = (Style)FindResource("Group");
        var any = false;
        foreach (var panel in CategoryPanels)
        {
            var found = false;
            foreach (UIElement child in panel.Children)
            {
                if (child is not FrameworkElement { Tag: string keywords } card)
                {
                    // the category's title and lead line stay as headings; group headings go
                    child.Visibility = child is FrameworkElement { Style: var s } && s == group ? Visibility.Collapsed : Visibility.Visible;
                    continue;
                }
                var text = $"{keywords} {card.ToolTip as string} {CardTitle(card)}";
                var match = words.All(w => text.Contains(w, StringComparison.CurrentCultureIgnoreCase));
                card.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                found |= match;
            }
            panel.Visibility = found ? Visibility.Visible : Visibility.Collapsed;
            any |= found;
        }
        NoMatch.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        ContentScroller.ScrollToTop();
    }

    /// <summary>A card's own title (the first line of its header).</summary>
    private static string CardTitle(FrameworkElement card) => card switch
    {
        Wpf.Ui.Controls.CardControl { Header: Panel p } => p.Children.OfType<Wpf.Ui.Controls.TextBlock>().FirstOrDefault()?.Text ?? "",
        Wpf.Ui.Controls.CardExpander { Header: Panel p } => p.Children.OfType<Wpf.Ui.Controls.TextBlock>().FirstOrDefault()?.Text ?? "",
        _ => "",
    };
}
