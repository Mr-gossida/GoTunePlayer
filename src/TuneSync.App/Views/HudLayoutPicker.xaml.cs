using System.Windows;
using System.Windows.Controls;
using TuneSync.Core.Models;

namespace TuneSync.App.Views;

/// <summary>Picks one of the HUD's layouts (see the XAML).</summary>
public partial class HudLayoutPicker : UserControl
{
    public static readonly DependencyProperty LayoutProperty = DependencyProperty.Register(
        nameof(Layout), typeof(HudLayout), typeof(HudLayoutPicker),
        new FrameworkPropertyMetadata(HudLayout.Card, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>A short line under each name (설정 has room for it, the HUD's menu doesn't).</summary>
    public static readonly DependencyProperty ShowDescriptionsProperty = DependencyProperty.Register(
        nameof(ShowDescriptions), typeof(bool), typeof(HudLayoutPicker), new PropertyMetadata(false));

    public HudLayoutPicker() => InitializeComponent();

    public HudLayout Layout
    {
        get => (HudLayout)GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    public bool ShowDescriptions
    {
        get => (bool)GetValue(ShowDescriptionsProperty);
        set => SetValue(ShowDescriptionsProperty, value);
    }
}
