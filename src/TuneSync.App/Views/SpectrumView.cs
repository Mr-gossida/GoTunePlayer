using System.Windows;
using System.Windows.Media;
using TuneSync.App.ViewModels;
using TuneSync.Core.Services;

namespace TuneSync.App.Views;

/// <summary>A spectrum analyzer in the spirit of Winamp's, but much denser: thin LED-segment bars
/// in the cover's accent color with peak caps that hang and fall, on a transparent background.
/// It reads the playing song's precomputed spectrum (<see cref="PlaybackViewModel.Spectrum"/>) at
/// the exact playing position every frame, and settles to nothing when playback stops.</summary>
public sealed class SpectrumView : FrameworkElement
{
    public static readonly DependencyProperty PlaybackProperty = DependencyProperty.Register(
        nameof(Playback), typeof(PlaybackViewModel), typeof(SpectrumView), new PropertyMetadata(null));

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(SpectrumView),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((SpectrumView)d)._fill = null));

    /// <summary>Bar width : gap. 3 px bars with 1 px gaps at the default.</summary>
    public static readonly DependencyProperty BarWidthProperty = DependencyProperty.Register(
        nameof(BarWidth), typeof(double), typeof(SpectrumView), new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public PlaybackViewModel? Playback
    {
        get => (PlaybackViewModel?)GetValue(PlaybackProperty);
        set => SetValue(PlaybackProperty, value);
    }

    /// <summary>The bars' color (the player's accent); they fade toward the bottom.</summary>
    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public double BarWidth
    {
        get => (double)GetValue(BarWidthProperty);
        set => SetValue(BarWidthProperty, value);
    }

    private const double Segment = 2, SegmentGap = 1; // LED segments: 2 px lit, 1 px dark
    private const float FallPerSecond = 1.6f;          // bars drop at this rate (full height / s)
    private const float PeakHoldSeconds = 0.45f, PeakFallPerSecond = 0.55f;

    private float[] _target = [];
    private float[] _level = [];
    private float[] _peak = [];
    private float[] _peakHold = [];
    private TimeSpan _lastFrame;
    private bool _subscribed;
    private bool _settled = true;
    private Brush? _fill;
    private Brush? _segments;

    public SpectrumView()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();
        IsVisibleChanged += (_, _) => { if (IsVisible) Subscribe(); else Unsubscribe(); };
    }

    private void Subscribe()
    {
        if (_subscribed || !IsVisible) return;
        _subscribed = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        _subscribed = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private int BarCount => Math.Max(8, (int)((ActualWidth + 1) / (BarWidth + 1)));

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _lastFrame) return;
        var dt = _lastFrame == TimeSpan.Zero ? 1 / 60f : (float)Math.Min((now - _lastFrame).TotalSeconds, 0.1);
        _lastFrame = now;

        var bars = BarCount;
        if (_level.Length != bars)
        {
            _target = new float[bars];
            _level = new float[bars];
            _peak = new float[bars];
            _peakHold = new float[bars];
        }

        // Where the song is right now; nothing when paused or before its spectrum is ready.
        var playback = Playback;
        var data = playback?.Spectrum;
        if (playback is { IsPlaying: true } && data is not null && playback.CurrentFile is not null)
            Resample(data, playback.CurrentPosition.TotalSeconds, _target);
        else
            Array.Clear(_target);

        var moving = false;
        for (var i = 0; i < bars; i++)
        {
            // Up at once, down at a steady fall: the classic analyzer motion.
            _level[i] = _target[i] >= _level[i] ? _target[i] : Math.Max(_target[i], _level[i] - FallPerSecond * dt);
            if (_level[i] >= _peak[i])
            {
                _peak[i] = _level[i];
                _peakHold[i] = PeakHoldSeconds;
            }
            else if ((_peakHold[i] -= dt) <= 0)
            {
                _peak[i] = Math.Max(_level[i], _peak[i] - PeakFallPerSecond * dt);
            }
            if (_level[i] > 0.001f || _peak[i] > 0.001f) moving = true;
        }

        // Nothing to draw and nothing falling: stop redrawing until there is.
        if (!moving && _settled) return;
        _settled = !moving;
        InvalidateVisual();
    }

    /// <summary>The song's bands spread over however many bars fit the width.</summary>
    private static void Resample(SpectrumData data, double seconds, float[] into)
    {
        Span<float> bands = stackalloc float[data.Bands];
        data.Sample(seconds, bands);
        for (var i = 0; i < into.Length; i++)
        {
            var position = (i + 0.5) * data.Bands / into.Length - 0.5;
            var b = Math.Clamp((int)Math.Floor(position), 0, data.Bands - 1);
            var t = (float)Math.Clamp(position - b, 0, 1);
            var next = Math.Min(b + 1, data.Bands - 1);
            into[i] = bands[b] * (1 - t) + bands[next] * t;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var height = ActualHeight;
        if (height <= 0 || _level.Length == 0) return;
        // The accent color glides to the next cover's color on a song change: follow it.
        var color = BarBrush is SolidColorBrush solid ? solid.Color : Colors.White;
        if (_fill is null || color != _fillColor) (_fill, _fillColor) = (BuildFill(color), color);
        _segments ??= BuildSegments();

        var step = BarWidth + 1;
        // Lit bars, cut into LED segments by an opacity mask (one mask for all bars: cheap).
        dc.PushOpacityMask(_segments);
        for (var i = 0; i < _level.Length; i++)
        {
            var h = Snap(_level[i] * height);
            if (h > 0) dc.DrawRectangle(_fill, null, new Rect(i * step, height - h, BarWidth, h));
        }
        dc.Pop();

        // Peak caps: one bright segment that hangs above the bar, then drops.
        for (var i = 0; i < _peak.Length; i++)
        {
            if (_peak[i] <= 0.02f) continue;
            var y = height - Snap(_peak[i] * height) - Segment - SegmentGap;
            if (y < 0) y = 0;
            dc.DrawRectangle(_fill, null, new Rect(i * step, y, BarWidth, Segment));
        }
    }

    /// <summary>Heights land on whole segments, like an LED meter.</summary>
    private static double Snap(double h) => Math.Floor(h / (Segment + SegmentGap)) * (Segment + SegmentGap);

    /// <summary>The accent color, brightest at the top and fading toward the bottom.</summary>
    private Color _fillColor;

    private static Brush BuildFill(Color color)
    {
        var top = Color.FromArgb(255, (byte)Math.Min(255, color.R + 40), (byte)Math.Min(255, color.G + 40), (byte)Math.Min(255, color.B + 40));
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            GradientStops =
            {
                new GradientStop(top, 0),
                new GradientStop(color, 0.45),
                new GradientStop(Color.FromArgb(150, color.R, color.G, color.B), 1),
            },
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>Horizontal stripes, 2 px opaque / 1 px clear, tiled down the control.</summary>
    private static Brush BuildSegments()
    {
        var group = new DrawingGroup();
        using (var g = group.Open())
        {
            g.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 1, Segment + SegmentGap));
            g.DrawRectangle(Brushes.Black, null, new Rect(0, SegmentGap, 1, Segment));
        }
        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 1, Segment + SegmentGap),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 1, Segment + SegmentGap),
            ViewboxUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
            AlignmentY = AlignmentY.Bottom,
        };
        brush.Freeze();
        return brush;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _segments = null;
        _settled = false;
    }
}
