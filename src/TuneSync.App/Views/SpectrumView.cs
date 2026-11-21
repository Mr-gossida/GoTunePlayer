using System.Windows;
using System.Windows.Media;
using System.Windows.Input;
using TuneSync.App.ViewModels;
using TuneSync.Core.Models;
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

    /// <summary>LED bars, round bars, a flowing curve, mirrored bars, dots - or nothing.</summary>
    public static readonly DependencyProperty LookProperty = DependencyProperty.Register(
        nameof(Look), typeof(SpectrumStyle), typeof(SpectrumView),
        new FrameworkPropertyMetadata(SpectrumStyle.Led, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((SpectrumView)d).OnLookChanged(e)));

    /// <summary>Clicking switches to the next look (under the song info).</summary>
    public static readonly DependencyProperty CycleOnClickProperty = DependencyProperty.Register(
        nameof(CycleOnClick), typeof(bool), typeof(SpectrumView),
        new FrameworkPropertyMetadata(false, (d, e) => ((SpectrumView)d).OnCycleOnClickChanged((bool)e.NewValue)));

    public SpectrumStyle Look
    {
        get => (SpectrumStyle)GetValue(LookProperty);
        set => SetValue(LookProperty, value);
    }

    public bool CycleOnClick
    {
        get => (bool)GetValue(CycleOnClickProperty);
        set => SetValue(CycleOnClickProperty, value);
    }

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

    public SpectrumView()
    {
        IsHitTestVisible = false;
        MouseEnter += (_, _) => InvalidateVisual();
        MouseLeave += (_, _) => InvalidateVisual();
        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();
        OnScreen.Watch(this, () => { if (OnScreen.Check(this)) Subscribe(); else Unsubscribe(); });
    }

    private void OnCycleOnClickChanged(bool on)
    {
        IsHitTestVisible = on;
        Cursor = on ? Cursors.Hand : null;
        ToolTip = on ? "클릭해서 스펙트럼 모양 바꾸기" : null;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!CycleOnClick || Playback is not { } playback) return;
        playback.CycleSpectrumStyle();
        // The new look's name, briefly (with 무작위, which look it picked).
        _label = playback.SpectrumStyle == SpectrumStyle.Random
            ? "무작위 · " + PlaybackViewModel.SpectrumStyleName(playback.SpectrumLook)
            : PlaybackViewModel.SpectrumStyleName(playback.SpectrumStyle);
        _labelUntil = DateTime.UtcNow.AddSeconds(1.4);
        _settled = false;
        InvalidateVisual();
        e.Handled = true;
    }

    // The new look's name, shown briefly after switching.
    private string? _label;
    private DateTime _labelUntil;

    private void OnLookChanged(DependencyPropertyChangedEventArgs e)
    {
        _settled = false;
        InvalidateVisual();
    }

    private void Subscribe()
    {
        if (_subscribed || !OnScreen.Check(this)) return;
        _subscribed = true;
        _lastFrame = TimeSpan.Zero;
        FrameTicker.Add(OnRendering);
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        _subscribed = false;
        FrameTicker.Remove(OnRendering);
    }

    private int BarCount => Math.Max(8, (int)((ActualWidth + 1) / (BarWidth + 1)));

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = FrameTicker.Now;
        if (now == _lastFrame) return;
        var dt = _lastFrame == TimeSpan.Zero ? 1 / 60f : (float)Math.Min((now - _lastFrame).TotalSeconds, 0.1);
        _lastFrame = now;
        _scopeTime += dt;

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

        // Nothing to draw and nothing falling: stop redrawing until there is. (The scope's
        // afterglow still fades out after the trace has gone flat.)
        if (_label is not null) moving = true;
        if (Look == SpectrumStyle.Scope && _trails.Count > 0) moving = true;
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
        if (CycleOnClick) dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // the whole strip takes clicks
        RenderLook(dc);
        DrawLabel(dc); // on top of the bars
    }

    private void RenderLook(DrawingContext dc)
    {
        var height = ActualHeight;
        if (Look == SpectrumStyle.Off)
        {
            // Off: only a faint hint while the pointer is over it, so it can be switched back on.
            // (Not while the new look's name is showing: the two would sit on top of each other.)
            if (CycleOnClick && IsMouseOver && _label is null) DrawHint(dc, "스펙트럼 꺼짐 · 클릭해서 켜기");
            return;
        }
        if (height <= 0 || _level.Length == 0) return;
        if (Look != SpectrumStyle.Led)
        {
            var c = BarBrush is SolidColorBrush b ? b.Color : Colors.White;
            if (_fill is null || c != _fillColor || height != _fillHeight) (_fill, _fillColor, _fillHeight) = (BuildFill(c, height), c, height);
            switch (Look)
            {
                case SpectrumStyle.Bars: DrawRoundBars(dc, height, mirrored: false); break;
                case SpectrumStyle.Mirror: DrawRoundBars(dc, height, mirrored: true); break;
                case SpectrumStyle.Wave: DrawCurve(dc, height, c); break;
                case SpectrumStyle.Dots: DrawDots(dc, height); break;
                case SpectrumStyle.Neon: DrawNeon(dc, height, c); break;
                case SpectrumStyle.Rainbow: DrawRainbow(dc, height); break;
                case SpectrumStyle.Reflect: DrawReflect(dc, height, c); break;
                case SpectrumStyle.Pins: DrawPins(dc, height, c); break;
                case SpectrumStyle.Scope: DrawScope(dc, height, c); break;
            }
            return;
        }
        // The accent color glides to the next cover's color on a song change: follow it.
        var color = BarBrush is SolidColorBrush solid ? solid.Color : Colors.White;
        if (_fill is null || color != _fillColor || height != _fillHeight) (_fill, _fillColor, _fillHeight) = (BuildFill(color, height), color, height);
        var step = BarWidth + 1;
        // Lit bars, as their LED segments drawn directly (not whole bars cut up by an opacity
        // mask, which had the GPU draw the strip into a separate surface every frame).
        const double pitch = Segment + SegmentGap;
        for (var i = 0; i < _level.Length; i++)
        {
            var h = Snap(_level[i] * height);
            if (h <= 0) continue;
            var top = height - h;
            var x = i * step;
            var bar = new StreamGeometry();
            using (var g = bar.Open())
            {
                // The same rows the mask let through: lit from 1 px into each 3 px step, from the top.
                for (var y = Math.Floor((top - SegmentGap) / pitch) * pitch + SegmentGap; y < height; y += pitch)
                {
                    double y0 = Math.Max(y, top), y1 = Math.Min(y + Segment, height);
                    if (y1 <= y0) continue;
                    g.BeginFigure(new Point(x, y0), true, true);
                    g.PolyLineTo([new Point(x + BarWidth, y0), new Point(x + BarWidth, y1), new Point(x, y1)], false, false);
                }
            }
            bar.Freeze();
            dc.DrawGeometry(_fill, null, bar);
        }

        // Peak caps: one bright segment that hangs above the bar, then drops.
        for (var i = 0; i < _peak.Length; i++)
        {
            if (_peak[i] <= 0.02f) continue;
            var y = height - Snap(_peak[i] * height) - Segment - SegmentGap;
            if (y < 0) y = 0;
            dc.DrawRectangle(_fill, null, new Rect(i * step, y, BarWidth, Segment));
        }
    }

    /// <summary>Smooth bars with rounded ends; mirrored ones grow both ways from the middle.</summary>
    private void DrawRoundBars(DrawingContext dc, double height, bool mirrored)
    {
        var step = BarWidth + 1;
        var radius = Math.Min(BarWidth / 2, 2);
        for (var i = 0; i < _level.Length; i++)
        {
            var h = Math.Max(_level[i] * height, _level[i] > 0.01f ? BarWidth : 0);
            if (h <= 0) continue;
            var top = mirrored ? (height - h) / 2 : height - h;
            dc.DrawRoundedRectangle(_fill, null, new Rect(i * step, top, BarWidth, h), radius, radius);
        }
    }

    /// <summary>A flowing filled curve through the bars' heights, with a bright edge.</summary>
    private void DrawCurve(DrawingContext dc, double height, Color color)
    {
        var step = BarWidth + 1;
        var n = _level.Length;
        if (n < 2) return;
        Point P(int i) => new(i * step + BarWidth / 2, height - Math.Min(height - 1, _level[i] * height));
        var edge = new StreamGeometry();
        var area = new StreamGeometry();
        using (var e = edge.Open())
        using (var a = area.Open())
        {
            e.BeginFigure(P(0), false, false);
            a.BeginFigure(new Point(P(0).X, height), true, true);
            a.LineTo(P(0), true, false);
            for (var i = 1; i < n; i++)
            {
                // Through the midpoints, with each bar's top as the control point: a smooth line.
                var prev = P(i - 1);
                var here = P(i);
                var midPoint = new Point((prev.X + here.X) / 2, (prev.Y + here.Y) / 2);
                e.QuadraticBezierTo(prev, midPoint, true, true);
                a.QuadraticBezierTo(prev, midPoint, true, true);
            }
            e.LineTo(P(n - 1), true, true);
            a.LineTo(P(n - 1), true, true);
            a.LineTo(new Point(P(n - 1).X, height), true, false);
        }
        edge.Freeze();
        area.Freeze();
        var fade = new LinearGradientBrush(Color.FromArgb(150, color.R, color.G, color.B), Color.FromArgb(10, color.R, color.G, color.B), 90);
        fade.Freeze();
        dc.DrawGeometry(fade, null, area);
        var bright = Color.FromRgb((byte)Math.Min(255, color.R + 50), (byte)Math.Min(255, color.G + 50), (byte)Math.Min(255, color.B + 50));
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(bright), 1.6) { LineJoin = PenLineJoin.Round }, edge);
    }

    // Brushes and pens for the newer looks, made once per color/size (not per frame).
    private Color _extrasColor;
    private Size _extrasSize;
    private Pen? _neonWide, _neonMid, _neonCore;
    private Brush? _rainbow, _reflection, _bright;
    private Pen? _ring;

    private void EnsureExtras(Color color, double height)
    {
        var size = new Size(ActualWidth, height);
        if (_neonWide is not null && color == _extrasColor && size == _extrasSize) return;
        (_extrasColor, _extrasSize) = (color, size);

        Pen MakePen(byte alpha, Color c, double thickness)
        {
            var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            b.Freeze();
            var p = new Pen(b, thickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            p.Freeze();
            return p;
        }
        var light = Lighten(color, 70);
        // A glow made of wide, faint strokes under a bright thin one (no blur effect: that would
        // have the GPU blur the whole strip every frame).
        _neonWide = MakePen(36, color, 7);
        _neonMid = MakePen(90, color, 3.5);
        _neonCore = MakePen(255, light, 1.4);
        _ring = MakePen(230, light, 1.2);

        var bright = new SolidColorBrush(light);
        bright.Freeze();
        _bright = bright;

        // One hue sweep across the whole strip (absolute, built once): each bar gets its own color.
        var rainbow = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(Math.Max(1, size.Width), 0) };
        for (var k = 0; k <= 6; k++) rainbow.GradientStops.Add(new GradientStop(FromHue(k * 300.0 / 6), k / 6.0));
        rainbow.Freeze();
        _rainbow = rainbow;

        // The reflection fades out going down from the floor.
        var floor = height * ReflectFloor;
        var reflection = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, floor),
            EndPoint = new Point(0, Math.Max(floor + 1, height)),
            GradientStops = { new GradientStop(Color.FromArgb(150, color.R, color.G, color.B), 0), new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1) },
        };
        reflection.Freeze();
        _reflection = reflection;
    }

    private static Color Lighten(Color c, int by) =>
        Color.FromArgb(c.A, (byte)Math.Min(255, c.R + by), (byte)Math.Min(255, c.G + by), (byte)Math.Min(255, c.B + by));

    private static Color FromHue(double hue)
    {
        // Full saturation, a little softened so it sits well on the aurora and dark themes.
        double h = hue / 60, x = 1 - Math.Abs(h % 2 - 1);
        (double r, double g, double b) = (int)h switch { 0 => (1d, x, 0d), 1 => (x, 1d, 0d), 2 => (0d, 1d, x), 3 => (0d, x, 1d), 4 => (x, 0d, 1d), _ => (1d, 0d, x) };
        return Color.FromRgb((byte)(70 + 185 * r), (byte)(70 + 185 * g), (byte)(70 + 185 * b));
    }

    /// <summary>The bars' tops as one smooth line (through the midpoints between neighbors).</summary>
    private StreamGeometry BuildTopLine(double height)
    {
        var step = BarWidth + 1;
        var n = _level.Length;
        Point P(int i) => new(i * step + BarWidth / 2, height - 2 - Math.Min(height - 4, _level[i] * (height - 4)));
        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(P(0), false, false);
            for (var i = 1; i < n; i++)
            {
                var prev = P(i - 1);
                var here = P(i);
                g.QuadraticBezierTo(prev, new Point((prev.X + here.X) / 2, (prev.Y + here.Y) / 2), true, true);
            }
            g.LineTo(P(n - 1), true, true);
        }
        line.Freeze();
        return line;
    }

    // ---- 오실로스코프 ----

    private double _scopeTime;
    private readonly List<StreamGeometry> _trails = [];
    private const int TrailLength = 4;
    private Pen? _scopeGrid;
    private Pen?[] _trailPens = [];
    private Color _scopeColor;

    /// <summary>A trace across the strip like an oscilloscope's, with the green-screen phosphor's
    /// afterglow: the last few traces linger, fainter each, behind the bright one. The song's
    /// spectrum (not its samples, which aren't kept) drives it: each band is a wave of its own
    /// pitch - low bands long and slow, high ones short and quick - at its loudness, and the sum is
    /// the trace, so bass makes it heave and cymbals make it fizz. Flat and dim when quiet.</summary>
    private void DrawScope(DrawingContext dc, double height, Color color)
    {
        var width = ActualWidth;
        if (width < 4) return;
        EnsureExtras(color, height);
        if (_scopeGrid is null || color != _scopeColor)
        {
            _scopeColor = color;
            var grid = new SolidColorBrush(Color.FromArgb(28, color.R, color.G, color.B));
            grid.Freeze();
            _scopeGrid = new Pen(grid, 1) { DashStyle = new DashStyle([2, 4], 0) };
            _scopeGrid.Freeze();
            _trailPens = new Pen?[TrailLength];
            for (var k = 0; k < TrailLength; k++)
            {
                var b = new SolidColorBrush(Color.FromArgb((byte)(110 * Math.Pow(0.55, k)), color.R, color.G, color.B));
                b.Freeze();
                var pen = new Pen(b, 1.3) { LineJoin = PenLineJoin.Round };
                pen.Freeze();
                _trailPens[k] = pen;
            }
        }

        var mid = Math.Round(height / 2) + 0.5;
        dc.DrawLine(_scopeGrid, new Point(0, mid), new Point(width, mid));

        // the bands, gathered into a dozen voices (each a wave of one pitch)
        const int voices = 12;
        Span<double> amp = stackalloc double[voices];
        double total = 0;
        for (var i = 0; i < _level.Length; i++)
        {
            var v = Math.Min(voices - 1, i * voices / _level.Length);
            amp[v] += _level[i];
        }
        for (var v = 0; v < voices; v++)
        {
            amp[v] /= Math.Max(1, _level.Length / voices);
            // the highs are fainter in any spectrum; lift them so they show as fizz
            amp[v] *= 0.75 + 0.5 * v / (voices - 1);
            total += amp[v];
        }

        var trace = new StreamGeometry();
        if (total > 0.002)
        {
            var room = height / 2 - 2;
            // the loudness sets the swing; the sum of the voices is kept inside the strip
            var loud = Math.Clamp(total / voices * 2.2, 0, 1);
            var scale = 1.9 * room * Math.Sqrt(loud) / Math.Max(total, 0.001); // the voices rarely all peak together
            var steps = (int)Math.Clamp(width / 2, 32, 600);
            using var g = trace.Open();
            for (var n = 0; n <= steps; n++)
            {
                var x = width * n / steps;
                var u = (double)n / steps;
                var y = 0.0;
                for (var v = 0; v < voices; v++)
                {
                    if (amp[v] < 0.002) continue;
                    var cycles = 1.5 * Math.Pow(1.38, v);         // 1.5 to ~50 waves across the strip
                    var speed = 0.9 + 0.55 * v;                   // high voices race, low ones roll
                    y += amp[v] * Math.Sin((u * cycles + _scopeTime * speed * (v % 2 == 0 ? 1 : -1)) * Math.Tau + v * 1.7);
                }
                // pinned at the ends, as if triggered there
                var ends = Math.Min(1, Math.Min(u, 1 - u) * 14);
                var p = new Point(x, mid - Math.Clamp(y * scale, -room, room) * ends);
                if (n == 0) g.BeginFigure(p, false, false);
                else g.LineTo(p, true, true);
            }
        }
        trace.Freeze();

        // the afterglow: older traces, fainter
        for (var k = _trails.Count - 1; k >= 0; k--)
            dc.DrawGeometry(null, _trailPens[Math.Min(k, TrailLength - 1)], _trails[k]);

        if (!trace.IsEmpty())
        {
            dc.DrawGeometry(null, _neonWide, trace);
            dc.DrawGeometry(null, _neonMid, trace);
            dc.DrawGeometry(null, _neonCore, trace);
            _trails.Insert(0, trace);
        }
        else if (_trails.Count > 0)
        {
            _trails.RemoveAt(_trails.Count - 1); // gone quiet: the glow fades away
        }
        if (_trails.Count > TrailLength) _trails.RemoveAt(_trails.Count - 1);
    }

    /// <summary>Only the outline, glowing like a neon tube: brighter where the music is louder.</summary>
    private void DrawNeon(DrawingContext dc, double height, Color color)
    {
        if (_level.Length < 2) return;
        EnsureExtras(color, height);
        var line = BuildTopLine(height);
        dc.DrawGeometry(null, _neonWide, line);
        dc.DrawGeometry(null, _neonMid, line);
        dc.DrawGeometry(null, _neonCore, line);
    }

    /// <summary>Round bars in the colors of the rainbow, low to high notes, with thin peak caps.</summary>
    private void DrawRainbow(DrawingContext dc, double height)
    {
        EnsureExtras(_fillColor, height);
        var step = BarWidth + 1;
        var radius = Math.Min(BarWidth / 2, 2);
        for (var i = 0; i < _level.Length; i++)
        {
            var x = i * step;
            var h = Math.Max(_level[i] * height, _level[i] > 0.01f ? BarWidth : 0);
            if (h > 0) dc.DrawRoundedRectangle(_rainbow, null, new Rect(x, height - h, BarWidth, h), radius, radius);
            if (_peak[i] > 0.03f)
            {
                var y = Math.Max(0, height - _peak[i] * height - 3);
                dc.DrawRectangle(_rainbow, null, new Rect(x, y, BarWidth, 1.5));
            }
        }
    }

    /// <summary>Where the floor of 반사 막대 sits, as a fraction of the height.</summary>
    private const double ReflectFloor = 0.7;

    /// <summary>Bars standing on a glossy floor, each mirrored below it, fading away.</summary>
    private void DrawReflect(DrawingContext dc, double height, Color color)
    {
        EnsureExtras(color, height);
        var floor = Math.Round(height * ReflectFloor);
        var below = height - floor - 1;
        var step = BarWidth + 1;
        for (var i = 0; i < _level.Length; i++)
        {
            var h = _level[i] * floor;
            if (h < 0.5) continue;
            var x = i * step;
            dc.DrawRectangle(_fill, null, new Rect(x, floor - h, BarWidth, h));
            var r = Math.Min(below, h * 0.55);
            if (r > 0.5) dc.DrawRectangle(_reflection, null, new Rect(x, floor + 1, BarWidth, r));
        }
    }

    /// <summary>A thin stem with a round head on top for each band; the peak hangs above as a small ring.</summary>
    private void DrawPins(DrawingContext dc, double height, Color color)
    {
        EnsureExtras(color, height);
        var step = BarWidth + 1;
        var r = Math.Max(1.2, BarWidth / 2);
        var stem = Math.Max(1, Math.Round(BarWidth / 3));
        for (var i = 0; i < _level.Length; i++)
        {
            var cx = i * step + BarWidth / 2;
            var top = height - r - _level[i] * (height - 2 * r);
            if (_level[i] > 0.01f)
            {
                dc.DrawRectangle(_fill, null, new Rect(cx - stem / 2, top, stem, height - top));
                dc.DrawEllipse(_bright, null, new Point(cx, top), r, r);
            }
            if (_peak[i] > 0.04f && _peak[i] - _level[i] > 0.03f)
            {
                var py = Math.Max(r, height - r - _peak[i] * (height - 2 * r));
                dc.DrawEllipse(null, _ring, new Point(cx, py), r * 0.8, r * 0.8);
            }
        }
    }

    /// <summary>Columns of round dots, with the peak as a dot hanging above.</summary>
    private void DrawDots(DrawingContext dc, double height)
    {
        var step = BarWidth + 1;
        var size = BarWidth;
        var pitch = size + 1;
        var r = size / 2;
        for (var i = 0; i < _level.Length; i++)
        {
            var x = i * step + r;
            var count = (int)Math.Floor(_level[i] * height / pitch);
            for (var k = 0; k < count; k++)
                dc.DrawEllipse(_fill, null, new Point(x, height - r - k * pitch), r, r);
            if (_peak[i] > 0.02f)
            {
                var y = height - r - Math.Max(count, (int)Math.Floor(_peak[i] * height / pitch)) * pitch;
                if (y >= r) dc.DrawEllipse(_fill, null, new Point(x, y), r, r);
            }
        }
    }

    private void DrawLabel(DrawingContext dc)
    {
        if (_label is null) return;
        var left = (_labelUntil - DateTime.UtcNow).TotalSeconds;
        if (left <= 0)
        {
            _label = null;
            return;
        }
        var alpha = Math.Clamp(left / 0.4, 0, 1);
        dc.PushOpacity(alpha);
        DrawHint(dc, _label, strong: true);
        dc.Pop();
    }

    /// <summary>The new look's name (<paramref name="strong"/>: white on a dark pill, readable on
    /// any background) or the quiet "click to turn on" hint (the theme's secondary text color).</summary>
    private void DrawHint(DrawingContext dc, string text, bool strong = false)
    {
        var brush = strong
            ? Brushes.White
            : TryFindResource("TextFillColorSecondaryBrush") as Brush ?? Brushes.Gray;
        var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Malgun Gothic"), FontStyles.Normal, strong ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            12, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var padX = strong ? 14 : 0;
        var w = formatted.Width + padX * 2;
        var h = formatted.Height + (strong ? 8 : 0);
        var rect = new Rect(Math.Max(0, (ActualWidth - w) / 2), Math.Max(0, (ActualHeight - h) / 2), w, h);
        if (strong) dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xE6, 0x1E, 0x1E, 0x22)), null, rect, h / 2, h / 2);
        dc.DrawText(formatted, new Point(rect.X + padX, rect.Y + (h - formatted.Height) / 2));
    }

    /// <summary>Heights land on whole segments, like an LED meter.</summary>
    private static double Snap(double h) => Math.Floor(h / (Segment + SegmentGap)) * (Segment + SegmentGap);

    /// <summary>The accent color, brightest at the top and fading toward the bottom.</summary>
    private Color _fillColor;
    private double _fillHeight;

    private static Brush BuildFill(Color color, double height)
    {
        var top = Color.FromArgb(255, (byte)Math.Min(255, color.R + 40), (byte)Math.Min(255, color.G + 40), (byte)Math.Min(255, color.B + 40));
        var brush = new LinearGradientBrush
        {
            // Over the strip's full height, the same for every bar: one gradient the GPU makes once.
            // (Relative to each bar, it had to be made again for every bar on every frame, as their
            // heights change - with three spectrums, most of the screen saver's GPU use.) Tall bars
            // get the bright top, low ones stay deeper, like an LED meter.
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, Math.Max(1, height)),
            MappingMode = BrushMappingMode.Absolute,
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


    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _settled = false;
    }
}
