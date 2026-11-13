using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace TuneSync.App.Views;

/// <summary>Square tiles in rows, only the visible ones built (the play list's grid view holds
/// a whole library). Tiles are about <see cref="TileSize"/> wide and stretch so every row fills
/// the width exactly; scrolling is by pixel.</summary>
public sealed class VirtualizingTilePanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty TileSizeProperty = DependencyProperty.Register(
        nameof(TileSize), typeof(double), typeof(VirtualizingTilePanel),
        new FrameworkPropertyMetadata(132.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The wished-for tile width; the real one is the panel width split evenly.</summary>
    public double TileSize
    {
        get => (double)GetValue(TileSizeProperty);
        set => SetValue(TileSizeProperty, value);
    }

    private int _columns = 1;
    private double _tile = 132;
    private Size _extent;
    private Size _viewport;
    private Point _offset;
    private int ItemCount => ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? TileSize * 3 : available.Width;
        _columns = Math.Max(1, (int)Math.Floor(width / Math.Max(48, TileSize)));
        _tile = Math.Floor(width / _columns);
        var count = ItemCount;
        var rows = (int)Math.Ceiling(count / (double)_columns);
        var viewport = new Size(width, double.IsInfinity(available.Height) ? rows * _tile : available.Height);
        UpdateScrollInfo(viewport, new Size(width, rows * _tile));

        // The rows on screen, plus one above and below so scrolling never shows a gap.
        var firstRow = Math.Max(0, (int)Math.Floor(_offset.Y / _tile) - 1);
        var lastRow = (int)Math.Ceiling((_offset.Y + _viewport.Height) / _tile);
        var first = Math.Min(count, firstRow * _columns);
        var last = Math.Min(count - 1, (lastRow + 1) * _columns - 1);

        var children = InternalChildren; // touch first: it sets up the generator
        var generator = ItemContainerGenerator;
        if (count > 0 && last >= first)
        {
            var start = generator.GeneratorPositionFromIndex(first);
            var childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
            using (generator.StartAt(start, GeneratorDirection.Forward, true))
            {
                for (var i = first; i <= last; i++, childIndex++)
                {
                    if (generator.GenerateNext(out var isNew) is not UIElement child) continue;
                    if (isNew)
                    {
                        if (childIndex >= children.Count) AddInternalChild(child);
                        else InsertInternalChild(childIndex, child);
                        generator.PrepareItemContainer(child);
                    }
                    child.Measure(new Size(_tile, _tile));
                }
            }
        }
        CleanUp(first, last);
        return new Size(width, _viewport.Height);
    }

    /// <summary>Lets go of tiles scrolled out of view.</summary>
    private void CleanUp(int first, int last)
    {
        var children = InternalChildren;
        var generator = ItemContainerGenerator;
        for (var i = children.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            var index = generator.IndexFromGeneratorPosition(position);
            if (index >= first && index <= last) continue;
            generator.Remove(position, 1);
            RemoveInternalChildRange(i, 1);
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var generator = ItemContainerGenerator;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index < 0) continue;
            var row = index / _columns;
            var column = index % _columns;
            InternalChildren[i].Arrange(new Rect(column * _tile, row * _tile - _offset.Y, _tile, _tile));
        }
        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Move:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
        }
        base.OnItemsChanged(sender, args);
    }

    /// <summary>Used by ListBox.ScrollIntoView: scrolls the tile's row into view.</summary>
    protected override void BringIndexIntoView(int index)
    {
        if (index < 0 || _columns <= 0) return;
        var top = index / _columns * _tile;
        if (top < _offset.Y) SetVerticalOffset(top);
        else if (top + _tile > _offset.Y + _viewport.Height) SetVerticalOffset(top + _tile - _viewport.Height);
    }

    /// <summary>The index of the tile at a point in this panel's own coordinates (drag and drop).</summary>
    public int IndexAt(Point point)
    {
        var column = Math.Clamp((int)(point.X / _tile), 0, _columns - 1);
        var row = (int)((point.Y + _offset.Y) / _tile);
        return row * _columns + column;
    }

    // ---- IScrollInfo ----

    private void UpdateScrollInfo(Size viewport, Size extent)
    {
        var changed = viewport != _viewport || extent != _extent;
        _viewport = viewport;
        _extent = extent;
        var max = Math.Max(0, _extent.Height - _viewport.Height);
        if (_offset.Y > max) _offset.Y = max;
        if (changed) ScrollOwner?.InvalidateScrollInfo();
    }

    public bool CanVerticallyScroll { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer? ScrollOwner { get; set; }

    private const double Line = 40;
    public void LineUp() => SetVerticalOffset(_offset.Y - Line);
    public void LineDown() => SetVerticalOffset(_offset.Y + Line);
    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);
    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - Line * 3);
    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + Line * 3);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void SetHorizontalOffset(double offset) { }

    public void SetVerticalOffset(double offset)
    {
        offset = Math.Clamp(offset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        if (Math.Abs(offset - _offset.Y) < 0.1) return;
        _offset.Y = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            if (!ReferenceEquals(InternalChildren[i], visual) && !InternalChildren[i].IsAncestorOf(visual)) continue;
            BringIndexIntoView(ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0)));
            break;
        }
        return rectangle;
    }
}
