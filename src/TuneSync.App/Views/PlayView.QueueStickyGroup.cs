using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TuneSync.App.ViewModels;
using TuneSync.Core.Models;

namespace TuneSync.App.Views;

// The heading of the group being scrolled through, pinned at the top of the play list.
public partial class PlayView
{
    // --- the group being scrolled through, pinned at the top of the queue ---

    private const double StickyHeight = 30; // the pinned heading's band, where the list fades out

    /// <summary>Shows which group the top of the queue is in. Hidden at a group's start, where
    /// the list's own heading is in view; the next group's heading pushes it up and takes over,
    /// like a phone's contact list.</summary>
    private void UpdateStickyGroup()
    {
        void Hide()
        {
            if (StickyGroup.Visibility == System.Windows.Visibility.Collapsed) return;
            StickyGroup.Visibility = System.Windows.Visibility.Collapsed;
            PlaylistBox.OpacityMask = null;
        }

        if (_viewModel.Library.PlaylistGroup == PlaylistGroupKey.None || ItemsHost(PlaylistBox) is not { } panel)
        {
            Hide();
            return;
        }

        // The realized rows/tiles, top to bottom.
        var rows = new List<(ListBoxItem Item, LibraryFileViewModel Row, double Top, double Bottom)>();
        foreach (var child in panel.Children)
        {
            if (child is not ListBoxItem item || !item.IsVisible || item.DataContext is not LibraryFileViewModel row) continue;
            var top = item.TranslatePoint(new System.Windows.Point(0, 0), PlaylistBox).Y;
            rows.Add((item, row, top, top + item.ActualHeight));
        }
        rows.Sort((a, b) => a.Top.CompareTo(b.Top));
        var first = rows.FirstOrDefault(r => r.Bottom > 1);
        if (first.Row?.GroupName is not { } group)
        {
            Hide();
            return;
        }

        // At a group's first row with its heading fully in view: that heading says it already.
        static bool HasHeading(ListBoxItem item) =>
            item.Template?.FindName("GroupHeading", item) is System.Windows.FrameworkElement { IsVisible: true };
        if (first.Row.GroupHeader is not null && HasHeading(first.Item) && first.Top >= -1)
        {
            Hide();
            return;
        }

        // The next group's heading coming up pushes this one out.
        var shift = 0.0;
        var next = rows.FirstOrDefault(r => r.Top > first.Top + 0.5 && r.Row.GroupHeader is not null && HasHeading(r.Item));
        if (next.Item is not null && next.Top < StickyHeight) shift = Math.Max(-StickyHeight, next.Top - StickyHeight);

        StickyGroupName.Text = new Converters.PathLeafConverter().Convert(group, typeof(string), null!, System.Globalization.CultureInfo.CurrentCulture) as string ?? group;
        StickyGroupCount.Text = $"{first.Row.GroupCount}곡";
        StickyShift.Y = shift;
        StickyGroup.Visibility = System.Windows.Visibility.Visible;

        // The list fades out under the pinned heading.
        var band = Math.Max(0, StickyHeight + shift);
        var mask = new System.Windows.Media.LinearGradientBrush { MappingMode = System.Windows.Media.BrushMappingMode.Absolute, StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(0, band + 10) };
        mask.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Colors.Transparent, 0));
        mask.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Colors.Transparent, band / (band + 10)));
        mask.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Colors.Black, 1));
        mask.Freeze();
        PlaylistBox.OpacityMask = mask;
    }
}
