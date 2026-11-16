using System.Windows.Media;
using System.Windows.Threading;
using TuneSync.App.Network;
using TuneSync.Core.Audio;

namespace TuneSync.App.Audio;

/// <summary>What <see cref="ViewModels.PlaybackViewModel"/> needs from whatever makes the sound.
/// One engine plays one song: a new one is made for each song opened. Events arrive on the UI thread.</summary>
internal interface IPlayerEngine
{
    /// <summary>The song is open (length known) and ready to play.</summary>
    event Action<IPlayerEngine>? MediaOpened;

    event Action<IPlayerEngine>? MediaEnded;

    event Action<IPlayerEngine, PlayerFailure>? MediaFailed;

    /// <summary>Whether the equalizer and tone controls reach this engine's sound.</summary>
    bool HasTone { get; }

    void Open();
    void Play();
    void Pause();

    /// <summary>Pauses and goes back to the start.</summary>
    void Stop();

    /// <summary>Stops and lets go of the file.</summary>
    void Close();

    TimeSpan Position { get; set; }

    /// <summary>Null until the song is open, or when its length can't be told.</summary>
    TimeSpan? NaturalDuration { get; }

    double Volume { get; set; }
    bool IsMuted { get; set; }
}

/// <param name="CanFallBack">The other engine may still be able to play this song.</param>
/// <param name="WasOpen">It failed while playing rather than while opening.</param>
internal sealed record PlayerFailure(Exception? Error, bool CanFallBack, bool Unsupported, bool WasOpen);

/// <summary>What a song is opened from: a local file, or a network song in the streaming cache.</summary>
internal sealed class PlaySource : IProgressiveSource
{
    private readonly LocalStreamServer? _server;
    private Uri? _url;

    public PlaySource(string path) => LocalPath = path;

    public PlaySource(CacheItem item, LocalStreamServer server)
    {
        Cache = item;
        _server = server;
    }

    public string? LocalPath { get; }
    public CacheItem? Cache { get; }
    public bool IsStream => Cache is not null;

    /// <summary>What Windows' media player opens: the file, or the song's address on the local stream server.</summary>
    public Uri Uri => LocalPath is not null ? new Uri(LocalPath) : _url ??= _server!.UrlFor(Cache!);

    string IProgressiveSource.Url => Uri.AbsoluteUri;

    string? IProgressiveSource.CompletedPath => Cache is { IsComplete: true } item ? item.FilePath : null;

    bool IProgressiveSource.HasReached(double fraction)
    {
        if (Cache is not { Length: > 0 } item) return false;
        // Some way past the spot: the tags at the front and a varying bitrate shift where a time sits in the file.
        return item.IsComplete || item.Downloaded >= Math.Min(item.Length, (long)(item.Length * fraction) + 768 * 1024);
    }

    bool IProgressiveSource.IsBroken => Cache is { IsComplete: false, IsDownloading: false };
}

/// <summary>Stands in before the first song is opened.</summary>
internal sealed class NoEngine : IPlayerEngine
{
    public event Action<IPlayerEngine>? MediaOpened { add { } remove { } }
    public event Action<IPlayerEngine>? MediaEnded { add { } remove { } }
    public event Action<IPlayerEngine, PlayerFailure>? MediaFailed { add { } remove { } }
    public bool HasTone => false;
    public void Open() { }
    public void Play() { }
    public void Pause() { }
    public void Stop() { }
    public void Close() { }
    public TimeSpan Position { get => TimeSpan.Zero; set { } }
    public TimeSpan? NaturalDuration => null;
    public double Volume { get; set; }
    public bool IsMuted { get; set; }
}

/// <summary>The app's own player: decodes with Windows' codecs and runs the sound through the
/// equalizer (<see cref="TonePlayer"/>).</summary>
internal sealed class ToneEngine : IPlayerEngine
{
    private readonly TonePlayer _player;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private bool _closed;

    public ToneEngine(PlaySource source, ToneConfig config)
    {
        _player = source.LocalPath is { } path ? new TonePlayer(path, config) : new TonePlayer(source, config);
        _player.Opened += () => Post(() => MediaOpened?.Invoke(this));
        _player.Ended += () => Post(() => MediaEnded?.Invoke(this));
        _player.Failed += (error, kind) =>
        {
            var wasOpen = _player.IsOpen;
            Post(() => MediaFailed?.Invoke(this, new PlayerFailure(error,
                CanFallBack: kind != TonePlayerFailure.Source, Unsupported: kind == TonePlayerFailure.Unsupported, wasOpen)));
        };
    }

    public event Action<IPlayerEngine>? MediaOpened;
    public event Action<IPlayerEngine>? MediaEnded;
    public event Action<IPlayerEngine, PlayerFailure>? MediaFailed;

    public bool HasTone => true;

    public ToneConfig Config { set => _player.Config = value; }

    /// <summary>How hard the limiter worked since the last call, in dB.</summary>
    public double TakeLimiterReductionDb() => _player.TakeLimiterReductionDb();

    public int Underruns => _player.Underruns;

    private void Post(Action action) => _dispatcher.BeginInvoke(() =>
    {
        if (!_closed) action();
    });

    public void Open() => _player.Open();
    public void Play() => _player.Play();
    public void Pause() => _player.Pause();

    public void Stop()
    {
        _player.Pause();
        _player.Position = TimeSpan.Zero;
    }

    public void Close()
    {
        _closed = true;
        _player.Close();
    }

    public TimeSpan Position
    {
        get => _player.Position;
        set => _player.Position = value;
    }

    public TimeSpan? NaturalDuration => _player.IsOpen && _player.Duration > TimeSpan.Zero ? _player.Duration : null;

    public double Volume
    {
        get => _player.Volume;
        set => _player.Volume = value;
    }

    public bool IsMuted
    {
        get => _player.IsMuted;
        set => _player.IsMuted = value;
    }
}

/// <summary>Windows' own media player (WPF's <see cref="MediaPlayer"/>): no equalizer, but it
/// plays some files the app's player can't, so it stays as the way back for those.</summary>
internal sealed class WindowsEngine : IPlayerEngine
{
    private readonly MediaPlayer _player = new();
    private readonly Uri _uri;
    private double _volume;
    private bool _muted;
    private bool _open;

    public WindowsEngine(PlaySource source)
    {
        _uri = source.Uri;
        _player.MediaOpened += (_, _) =>
        {
            _open = true;
            // MediaPlayer falls back to its default volume (0.5) whenever a source is opened.
            _player.Volume = _volume;
            _player.IsMuted = _muted;
            MediaOpened?.Invoke(this);
        };
        _player.MediaEnded += (_, _) => MediaEnded?.Invoke(this);
        _player.MediaFailed += (_, e) => MediaFailed?.Invoke(this, new PlayerFailure(e.ErrorException, CanFallBack: false, Unsupported: false, _open));
    }

    public event Action<IPlayerEngine>? MediaOpened;
    public event Action<IPlayerEngine>? MediaEnded;
    public event Action<IPlayerEngine, PlayerFailure>? MediaFailed;

    public bool HasTone => false;

    public void Open()
    {
        _player.Volume = _volume;
        _player.IsMuted = _muted;
        _player.Open(_uri);
    }

    public void Play() => _player.Play();
    public void Pause() => _player.Pause();
    public void Stop() => _player.Stop();
    public void Close() => _player.Close();

    public TimeSpan Position
    {
        get => _player.Position;
        set => _player.Position = value;
    }

    public TimeSpan? NaturalDuration => _player.NaturalDuration.HasTimeSpan ? _player.NaturalDuration.TimeSpan : null;

    public double Volume
    {
        get => _volume;
        set => _player.Volume = _volume = value;
    }

    public bool IsMuted
    {
        get => _muted;
        set => _player.IsMuted = _muted = value;
    }
}
