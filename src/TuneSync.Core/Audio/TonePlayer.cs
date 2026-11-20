using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using TuneSync.Core.Audio.Dsd;
using TuneSync.Core.Models;

namespace TuneSync.Core.Audio;

/// <summary>Why a <see cref="TonePlayer"/> gave up.</summary>
public enum TonePlayerFailure
{
    /// <summary>Windows' decoders can't read this file (or it broke off in the middle).</summary>
    Unsupported,
    /// <summary>A network song whose data stopped arriving.</summary>
    Source,
    /// <summary>No sound device that takes the stream.</summary>
    Device,
}

/// <summary>A song that is still arriving from the network.</summary>
public interface IProgressiveSource
{
    /// <summary>Where the song can be read from while it downloads (it may wait for bytes).</summary>
    string Url { get; }

    /// <summary>The finished local copy, once all of it is here.</summary>
    string? CompletedPath { get; }

    /// <summary>Whether the song has arrived up to this point (0..1 of its length).</summary>
    bool HasReached(double fraction);

    /// <summary>The download died: what is missing will never come.</summary>
    bool IsBroken { get; }

    /// <summary>The song's file name or path (what kind of file it is).</summary>
    string Name { get; }
}

/// <summary>The way the sound reaches the device.</summary>
public enum OutputPath
{
    /// <summary>Through Windows' mixer.</summary>
    Shared,
    /// <summary>To the device alone, through GoTune's processing.</summary>
    Exclusive,
    /// <summary>The file's samples, untouched.</summary>
    BitPerfect,
    /// <summary>A DSD file's own bits, as DoP.</summary>
    DoP,
    /// <summary>PCM turned into DSD, as DoP.</summary>
    Remaster,
}

/// <summary>How the sound is going out right now, piece by piece (for the play screen's badges).</summary>
/// <param name="Bits">The device format's bits (0 in shared mode, where Windows' mixer takes float).</param>
/// <param name="Rate">What goes to the device: its sample rate, or the DSD rate for DoP and remastering.</param>
/// <param name="Conversion">A rate change GoTune makes ("업샘플링 ×8", "DSD → PCM"), or null.</param>
/// <param name="ConversionDetail">"44.1 → 352.8kHz · 선형 위상".</param>
/// <param name="Dsd">"DSD64" for a DSD song or remastering.</param>
/// <param name="Equalizer">The equalizer is shaping the sound.</param>
/// <param name="Note">Why it isn't quite as the settings ask, when it isn't.</param>
public sealed record OutputInfo(OutputPath Path, int Bits, int Rate, int SongRate, string? Conversion, string? ConversionDetail,
    string? Dsd, bool Equalizer, bool HardwareVolume, string Layout, string? Note);

/// <summary>DoP can't go out the way the output is set now (not exclusive, the device won't take
/// it, or it has no volume control of its own): the song has to be opened again as PCM.</summary>
public sealed class DopUnavailableException(string message) : Exception(message);

/// <summary>Plays one song through the sound chain (<see cref="ToneProcessor"/>): a decoding
/// thread fills a short buffer of raw samples, and an output thread takes them from there, runs
/// the equalizer and hands them to Windows (WASAPI) - so a slider moved in the equalizer is heard
/// a fraction of a second later, and a slow disk or network never stalls the sound device. Where
/// the sound goes is <see cref="Output"/>: a chosen device or Windows' default, stereo or spread
/// over more speakers, shared with other programs or (exclusive mode) sent to the device alone at
/// a sample rate and bit depth it takes - the song's own rate when it can, resampled when not
/// (or upsampled on purpose, <see cref="OutputSettings.Upsampling"/>). From the ring on, the sound
/// is carried in 64-bit floating point all the way to the device's own sample format.
/// One player per song; <see cref="Close"/> releases the file.
/// Events arrive on the player's own threads.</summary>
public sealed class TonePlayer : IDisposable
{
    /// <summary>How much decoded sound is kept ready.</summary>
    private const double RingSeconds = 1.5;

    /// <summary>How much sound Windows holds: long enough to ride out a pause of the .NET
    /// runtime (garbage collection), short enough that equalizer changes feel immediate.</summary>
    private const int DeviceBufferMilliseconds = 200;

    private const int DecodeFrames = 4096;

    private readonly string? _path;
    private readonly IProgressiveSource? _source;
    private readonly object _lock = new();
    private readonly AutoResetEvent _renderWake = new(false);
    private readonly AutoResetEvent _decodeWake = new(false);
    private readonly AutoResetEvent _audioEvent = new(false);
    private Thread? _decodeThread, _renderThread;
    private volatile bool _closing, _wantPlay, _failed;

    // Decoded stereo frames waiting to be played (guarded by _lock).
    private float[] _ring = [];
    private int _ringStart, _ringCount, _ringCapacity;
    private bool _endOfSong;

    // Seeking: each request gets a number; the decoder and the output each note the one they
    // have caught up with, and sound only flows when all three agree.
    private long _seekSerial, _ringSerial, _renderSerial;
    private TimeSpan _seekTarget;

    // Position (guarded by _lock): frames taken since the last seek, and how many of them had
    // been heard at a known moment.
    private double _baseSeconds;
    private long _consumed;
    private double _anchorFrames, _lastReported;
    private long _anchorTicks;
    private bool _running;

    private int _sampleRate;
    private TimeSpan _duration;
    private ToneProcessor? _processor;
    private ToneConfig _config;
    private float _volume = 1;
    private bool _muted;

    // The sound device (output thread only).
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private AudioClient? _client;
    private AudioRenderClient? _render;
    private string? _deviceId;
    private int _bufferFrames;

    // How the sound goes out (output thread only): the device's rate and channels, the format the
    // samples are written in, and the resampler when the device runs at another rate.
    private OutputSettings _output = new();
    private volatile bool _reopen;
    private int _outRate, _outChannels;
    private double _ratio = 1; // device frames per song frame
    private bool _exclusiveOpen;
    // What the description says (output thread writes, any thread reads): the device part, why it
    // isn't as asked, and whether the sound goes out bit-perfect.
    private volatile string? _outputBase, _outputNote;
    private volatile bool _bitPerfect, _resampling;
    private volatile string? _resampleFilterName;

    // DSD: the song is DSD (and how it's read), and DSD remastering of PCM.
    private volatile bool _dop, _converted, _remastering;
    private string? _dsdName;
    private int _dsdRate;
    private double[] _silence = [];
    private volatile int _outBits;
    private volatile string _layoutName = "스테레오";
    private ResampleFilter _resampleFilter;
    private int _sourceBits;
    private ChannelLayout? _layout;
    private SampleWriter? _writer;
    private IResampler? _resampler;
    private double[] _expanded = [];
    private double[] _resampled = [];
    private byte[] _bytes = [];

    // Hardware volume (exclusive mode): the device's own volume control, and what Windows had it
    // at before, to put back when GoTune lets go.
    private AudioEndpointVolume? _endpointVolume;
    private string? _heldDeviceId;
    private float _savedLevel;
    private bool _savedMute;
    private volatile bool _hardwareVolume, _volumeDirty;
    private double _bitPerfectGain = 1; // the volume last applied to bit-perfect samples (output thread)

    public TonePlayer(string path, ToneConfig config, OutputSettings? output = null)
    {
        _path = path;
        _config = config;
        _output = output?.Clone() ?? new OutputSettings();
    }

    public TonePlayer(IProgressiveSource source, ToneConfig config, OutputSettings? output = null)
    {
        _source = source;
        _config = config;
        _output = output?.Clone() ?? new OutputSettings();
    }

    /// <summary>Where and how the sound goes out. Changing it while playing reopens the device
    /// (what it still held is dropped: the caller seeks back to the position to pick up there).</summary>
    public OutputSettings Output
    {
        get => _output.Clone();
        set
        {
            _output = value.Clone();
            _reopen = true;
            _renderWake.Set();
        }
    }

    /// <summary>How the sound is going out right now ("독점 · 24비트 96kHz · 5.1"), and why it
    /// isn't the way <see cref="Output"/> asks, when it can't be.</summary>
    public string? OutputDescription
    {
        get
        {
            if (_outputBase is not { } device) return null;
            var volume = _hardwareVolume ? "장치 볼륨" : "볼륨";
            if (_dop)
                return Join($"DoP · {_dsdName} ({_dsdRate / 1e6:0.0}MHz) 원음 그대로 · {device} · " + (_muted ? "음소거" : $"장치 볼륨 {Math.Round(_volume * 100)}%"), _outputNote);
            if (_bitPerfect && !_hardwareVolume && (_muted || _volume < 1))
            {
                // GoTune's volume always counts: below 100 % the samples are turned down, so
                // the sound isn't the file's own any more
                var turned = _muted ? "음소거" : $"볼륨 {Math.Round(_volume * 100)}%";
                return Join($"독점 · {device} · {turned}", _outputNote ?? "볼륨이 100%일 때 비트퍼펙트");
            }
            if (_bitPerfect)
            {
                var source = _sourceBits > 0 ? $"원본 {_sourceBits}비트 그대로" : "디코딩한 소리 그대로";
                var level = _hardwareVolume ? (_muted ? " · 음소거" : $" · 장치 볼륨 {Math.Round(_volume * 100)}%") : "";
                return Join($"비트퍼펙트 · {device} · {source}{level}", _outputNote);
            }
            // what changes the sound on the way, so "독점" isn't mistaken for untouched
            var applied = new List<string>();
            if (_converted && _dsdName is not null) applied.Add($"{_dsdName} → PCM {_sampleRate / 1000.0:0.#}kHz 변환");
            if (_resampling)
                applied.Add(_outRate > _sampleRate
                    ? $"업샘플링 {_sampleRate / 1000.0:0.#} → {_outRate / 1000.0:0.#}kHz · {_resampleFilterName}"
                    : $"곡 {_sampleRate / 1000.0:0.#}kHz에서 변환 · {_resampleFilterName}");
            if (!_exclusiveOpen) applied.Add("Windows 믹서 거침");
            if (!ReferenceEquals(_config, ToneConfig.Neutral)) applied.Add("이퀄라이저 적용");
            if (_muted) applied.Add("음소거");
            else if (_volume < 1 || _hardwareVolume) applied.Add($"{volume} {Math.Round(_volume * 100)}%");
            var text = applied.Count > 0 ? $"{device} ({string.Join(" · ", applied)})" : device;
            return Join(text, _outputNote);
        }
    }

    /// <summary>The samples reach the device exactly as the file holds them (bit-perfect mode,
    /// and the device took the song's own rate).</summary>
    public bool IsBitPerfect => _bitPerfect && (_hardwareVolume || (_volume >= 1 && !_muted));

    /// <summary>How the sound is going out right now, piece by piece (null until the device is open).</summary>
    public OutputInfo? Info
    {
        get
        {
            if (_outputBase is null) return null;
            var path = _dop ? OutputPath.DoP
                : _remastering ? OutputPath.Remaster
                : IsBitPerfect ? OutputPath.BitPerfect
                : _exclusiveOpen ? OutputPath.Exclusive
                : OutputPath.Shared;
            string? conversion = null, detail = null;
            if (_converted && _dsdName is not null)
            {
                conversion = "DSD → PCM";
                detail = $"{_dsdName} ({_dsdRate / 1e6:0.0}MHz) → PCM {_sampleRate / 1000.0:0.#}kHz";
            }
            else if (_resampling)
            {
                conversion = _outRate > _sampleRate
                    ? _outRate % _sampleRate == 0 ? $"업샘플링 ×{_outRate / _sampleRate}" : "업샘플링"
                    : "샘플 속도 변환";
                detail = $"{_sampleRate / 1000.0:0.#} → {_outRate / 1000.0:0.#}kHz · {_resampleFilterName}";
            }
            else if (_remastering && _resampler is DsdRemasterer remaster)
            {
                detail = $"PCM {_sampleRate / 1000.0:0.#}kHz → DSD{remaster.DsdRate / (remaster.DsdRate % 44100 == 0 ? 44100 : 48000)} ({remaster.DsdRate / 1e6:0.0}MHz) · {_resampleFilterName}";
            }
            var dsdRate = _dop ? _dsdRate : _resampler is DsdRemasterer r ? r.DsdRate : 0;
            var dsd = _dop ? _dsdName : dsdRate > 0 ? $"DSD{dsdRate / (dsdRate % 44100 == 0 ? 44100 : 48000)}" : _converted ? _dsdName : null;
            var note = _outputNote;
            if (_bitPerfect && !IsBitPerfect) note = note is null ? "볼륨이 100%일 때 비트퍼펙트" : $"{note} — 볼륨이 100%일 때 비트퍼펙트";
            var equalizer = path is not (OutputPath.BitPerfect or OutputPath.DoP) && !ReferenceEquals(_config, ToneConfig.Neutral);
            return new OutputInfo(path, _exclusiveOpen ? _outBits : 0, dsdRate > 0 ? dsdRate : _outRate, _sampleRate,
                conversion, detail, dsd, equalizer, _hardwareVolume, _layoutName, note);
        }
    }

    /// <summary>PCM is going out as DSD (DoP).</summary>
    public bool IsRemastering => _remastering;

    /// <summary>The song is a DSD file (known once it is open).</summary>
    public bool IsDsd { get; private set; }

    /// <summary>DoP can't be sent the way the output is set: open the song again with DSD as PCM.
    /// Nothing is played meanwhile.</summary>
    public event Action? RerouteNeeded;

    /// <summary>The device was (re)opened: <see cref="OutputDescription"/> may have changed.</summary>
    public event Action? OutputChanged;

    /// <summary>The song is open and its length known; nothing plays until <see cref="Play"/>.</summary>
    public event Action? Opened;

    /// <summary>The last sound of the song has been heard.</summary>
    public event Action? Ended;

    public event Action<Exception, TonePlayerFailure>? Failed;

    /// <summary>Whether <see cref="Opened"/> was reached.</summary>
    public bool IsOpen { get; private set; }

    public TimeSpan Duration => _duration;

    /// <summary>Times the sound device ran dry while playing (a glitch was heard).</summary>
    public int Underruns { get; private set; }

    public ToneConfig Config
    {
        get => _config;
        set
        {
            _config = value;
            if (_processor is { } processor) processor.Config = value;
        }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = (float)Math.Clamp(value, 0, 1);
            ApplyVolume();
        }
    }

    public bool IsMuted
    {
        get => _muted;
        set
        {
            _muted = value;
            ApplyVolume();
        }
    }

    private void ApplyVolume()
    {
        if (_hardwareVolume)
        {
            // the device turns it down; the samples go out whole (set on the output thread)
            if (_processor is { } p) p.Volume = 1;
            _volumeDirty = true;
            _renderWake.Set();
            return;
        }
        if (_processor is { } processor) processor.Volume = _muted ? 0 : _volume;
        OutputChanged?.Invoke(); // the description names the volume
    }

    /// <summary>The sound going out, for the live spectrum and meters (null until the song is opened).</summary>
    public OutputScope? Scope => _processor?.Scope;

    /// <summary>How hard the limiter worked since the last call, in dB (0 = not at all).</summary>
    public double TakeLimiterReductionDb() => _processor?.TakeLimiterReductionDb() ?? 0;

    public void Open()
    {
        if (_decodeThread is not null) return;
        _decodeThread = new Thread(DecodeLoop) { IsBackground = true, Name = "tone-decode" };
        _decodeThread.Start();
    }

    public void Play()
    {
        _wantPlay = true;
        _renderWake.Set();
    }

    public void Pause()
    {
        _wantPlay = false;
        _renderWake.Set();
    }

    /// <summary>What is being heard right now.</summary>
    public TimeSpan Position
    {
        get
        {
            lock (_lock)
            {
                if (_renderSerial != _seekSerial || _sampleRate == 0) return _seekTarget;
                var frames = _anchorFrames;
                if (_running) frames += (Stopwatch.GetTimestamp() - _anchorTicks) * (double)_sampleRate / Stopwatch.Frequency;
                frames = Math.Clamp(frames, 0, _consumed);
                // The estimate is re-anchored on every refill; never let that show as a step back.
                if (frames < _lastReported) frames = _lastReported;
                _lastReported = frames;
                return TimeSpan.FromSeconds(_baseSeconds + frames / _sampleRate);
            }
        }
        set
        {
            lock (_lock)
            {
                _seekTarget = value < TimeSpan.Zero ? TimeSpan.Zero : value;
                _seekSerial++;
            }
            _decodeWake.Set();
            _renderWake.Set();
        }
    }

    /// <summary>Stops and lets go of the file. Returns once it is released (or after a moment,
    /// if a network read is still on its way out).</summary>
    public void Close()
    {
        if (_closing) return;
        _closing = true;
        _renderWake.Set();
        _decodeWake.Set();
        if (_renderThread is { } render && render != Thread.CurrentThread) render.Join(400);
        if (_decodeThread is { } decode && decode != Thread.CurrentThread) decode.Join(400);
    }

    public void Dispose() => Close();

    private void Fail(Exception error, TonePlayerFailure kind)
    {
        if (_closing || _failed) return;
        _failed = true;
        _wantPlay = false;
        _renderWake.Set();
        Failed?.Invoke(error, kind);
    }

    // ---- decoding ----

    private void DecodeLoop()
    {
        IAudioDecoder? decoder = null;
        var fromUrl = false;
        try
        {
            try
            {
                var local = _path ?? _source!.CompletedPath;
                IsDsd = DsdFile.IsDsdPath(_path ?? _source!.Name);
                if (IsDsd && local is null)
                {
                    // DSD is read from the finished file (Windows has no DSD decoder to stream it)
                    local = WaitForDownload();
                    if (local is null) return;
                }
                fromUrl = local is null;
                decoder = IsDsd ? OpenDsd(local!) : new MfDecoder(local ?? _source!.Url);
                if (decoder.Channels > 8) throw new NotSupportedException("Too many channels.");
            }
            catch (Exception ex)
            {
                Fail(ex, _source is { IsBroken: true } ? TonePlayerFailure.Source : TonePlayerFailure.Unsupported);
                return;
            }

            _sampleRate = decoder.SampleRate;
            _sourceBits = decoder.SourceBits;
            _converted = decoder.IsConverted;
            _duration = decoder.Duration;
            _ringCapacity = (int)(_sampleRate * RingSeconds);
            _ring = new float[_ringCapacity * 2];
            _processor = new ToneProcessor(_sampleRate) { Config = _config };
            ApplyVolume();

            _renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "tone-render", Priority = ThreadPriority.Highest };
            _renderThread.Start();

            var channels = decoder.Channels;
            var decoded = new float[DecodeFrames * channels];
            var stereo = new float[DecodeFrames * 2];
            long handled = 0;
            var ended = false;
            while (!_closing && !_failed)
            {
                long serial;
                TimeSpan target;
                int free;
                lock (_lock)
                {
                    serial = _seekSerial;
                    target = _seekTarget;
                    free = _ringCapacity - _ringCount;
                }

                if (serial != handled)
                {
                    if (fromUrl)
                    {
                        if (!SeekWhileDownloading(ref decoder, ref fromUrl, target, serial)) continue; // closed, or a newer seek came in
                    }
                    else
                    {
                        decoder.Seek(target);
                    }
                    lock (_lock)
                    {
                        _ringStart = _ringCount = 0;
                        _ringSerial = serial;
                        _endOfSong = false;
                    }
                    handled = serial;
                    ended = false;
                    _renderWake.Set();
                    continue;
                }

                if (ended || free < DecodeFrames)
                {
                    _decodeWake.WaitOne(20);
                    continue;
                }

                var frames = decoder.Read(decoded);
                if (frames > 0) ToStereo(decoded, stereo, frames, channels);
                if (frames < DecodeFrames && _source is { IsBroken: true })
                    throw new IOException("The download stopped before the end of the song.");
                lock (_lock)
                {
                    if (_seekSerial != handled) continue; // sought meanwhile: these frames belong to the old place
                    Push(stereo, frames);
                    if (frames < DecodeFrames)
                    {
                        _endOfSong = true;
                        ended = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Fail(ex, fromUrl || _source is { IsBroken: true } ? TonePlayerFailure.Source : TonePlayerFailure.Unsupported);
        }
        finally
        {
            try { decoder?.Dispose(); } catch { /* already gone */ }
        }
    }

    /// <summary>A seek in a song that is still downloading. Windows can't seek in every format
    /// over http (FLAC fails outright), and never past what has arrived - so: once the download
    /// is complete, the finished file is opened instead and the seek made there; before that, the
    /// stream is tried if the wanted spot has arrived. False when closed or overtaken by a newer seek.</summary>
    private bool SeekWhileDownloading(ref IAudioDecoder decoder, ref bool fromUrl, TimeSpan target, long serial)
    {
        var source = _source!;
        var fraction = _duration > TimeSpan.Zero ? Math.Clamp(target.TotalSeconds / _duration.TotalSeconds, 0, 1) : 1;
        var streamTried = false;
        while (true)
        {
            if (_closing || _failed) return false;
            lock (_lock) if (_seekSerial != serial) return false;

            if (source.CompletedPath is { } path)
            {
                var file = new MfDecoder(path);
                if (file.SampleRate != decoder.SampleRate || file.Channels != decoder.Channels)
                {
                    file.Dispose();
                    throw new NotSupportedException("The downloaded file doesn't match its stream.");
                }
                decoder.Dispose();
                decoder = file;
                fromUrl = false;
                decoder.Seek(target);
                return true;
            }
            if (source.IsBroken) throw new IOException("The download stopped.");
            if (!streamTried && source.HasReached(fraction))
            {
                streamTried = true;
                try
                {
                    decoder.Seek(target);
                    return true;
                }
                catch (COMException)
                {
                    // not in this format: wait for the whole file
                }
            }
            Thread.Sleep(40);
        }
    }

    /// <summary>The finished file of a song still downloading (null once closed).</summary>
    private string? WaitForDownload()
    {
        while (!_closing && !_failed)
        {
            if (_source!.CompletedPath is { } path) return path;
            if (_source.IsBroken) throw new IOException("The download stopped.");
            Thread.Sleep(100);
        }
        return null;
    }

    /// <summary>A DSD file: as DoP when the output asks for it and the device can take it with a
    /// volume control of its own, else converted to PCM.</summary>
    private IAudioDecoder OpenDsd(string path)
    {
        var output = _output;
        var dop = false;
        if (output.Exclusive && output.DsdOutput == DsdOutput.DoP && output.HardwareVolume)
        {
            using var file = new DsdFile(path);
            dop = file.Channels == 2 && OutputDevices.CanCarryDoP(output.DeviceId, file.SampleRate / 16);
        }
        var decoder = new DsdDecoder(path, dop);
        _dop = decoder.IsDoP;
        _converted = !decoder.IsDoP;
        _dsdName = decoder.Name;
        _dsdRate = decoder.DsdRate;
        return decoder;
    }

    /// <summary>Mono is doubled; surround is folded down to two channels (front + center + its
    /// side's rears, turned down so the sum stays in range).</summary>
    private static void ToStereo(float[] source, float[] stereo, int frames, int channels)
    {
        switch (channels)
        {
            case 1:
                for (var i = 0; i < frames; i++) stereo[2 * i] = stereo[2 * i + 1] = source[i];
                break;
            case 2:
                Array.Copy(source, stereo, frames * 2);
                break;
            case 6 or 8: // FL FR FC LFE BL BR (SL SR)
                for (var i = 0; i < frames; i++)
                {
                    var at = i * channels;
                    var center = source[at + 2] * 0.5f;
                    var left = source[at] * 0.7f + center + source[at + 4] * 0.5f;
                    var right = source[at + 1] * 0.7f + center + source[at + 5] * 0.5f;
                    if (channels == 8)
                    {
                        left += source[at + 6] * 0.5f;
                        right += source[at + 7] * 0.5f;
                    }
                    stereo[2 * i] = left;
                    stereo[2 * i + 1] = right;
                }
                break;
            default: // unusual layouts: the front pair
                for (var i = 0; i < frames; i++)
                {
                    stereo[2 * i] = source[i * channels];
                    stereo[2 * i + 1] = source[i * channels + 1];
                }
                break;
        }
    }

    private void Push(float[] stereo, int frames)
    {
        var end = (_ringStart + _ringCount) % _ringCapacity;
        var first = Math.Min(frames, _ringCapacity - end);
        Array.Copy(stereo, 0, _ring, end * 2, first * 2);
        Array.Copy(stereo, first * 2, _ring, 0, (frames - first) * 2);
        _ringCount += frames;
    }

    /// <summary>Takes frames off the ring into the output's 64-bit buffer.</summary>
    private int Pull(double[] target, int frames)
    {
        frames = Math.Min(frames, _ringCount);
        var first = Math.Min(frames, _ringCapacity - _ringStart);
        for (var i = 0; i < first * 2; i++) target[i] = _ring[_ringStart * 2 + i];
        for (var i = 0; i < (frames - first) * 2; i++) target[first * 2 + i] = _ring[i];
        _ringStart = (_ringStart + frames) % _ringCapacity;
        _ringCount -= frames;
        return frames;
    }

    // ---- output ----

    private void RenderLoop()
    {
        uint task = 0;
        var mmcss = IntPtr.Zero;
        try { mmcss = AvSetMmThreadCharacteristics("Pro Audio", ref task); } catch { /* plain high priority then */ }
        try
        {
            try
            {
                OpenDevice();
            }
            catch (DopUnavailableException)
            {
                CloseDevice();
                RefuseDoP();
                return;
            }
            catch (Exception ex)
            {
                CloseDevice();
                Fail(ex, TonePlayerFailure.Device);
                return;
            }
            IsOpen = true;
            if (!_closing) Opened?.Invoke();

            var processor = _processor!;
            var buffer = EnsureBuffer([]);
            WaitHandle[] handles = [_audioEvent, _renderWake];
            long serial = 0, nextDeviceCheck = 0;
            bool started = false, ended = false, tailWritten = false, silenceAfterTail = false;
            while (!_closing && !_failed)
            {
                try
                {
                    if (_client is null)
                    {
                        // The device went away (unplugged, switched): take up the new default one.
                        try
                        {
                            OpenDevice();
                        }
                        catch (DopUnavailableException)
                        {
                            CloseDevice();
                            RefuseDoP();
                            return;
                        }
                        catch
                        {
                            CloseDevice();
                            _renderWake.WaitOne(500);
                            continue;
                        }
                        buffer = EnsureBuffer(buffer);
                        started = false;
                    }
                    if (_reopen)
                    {
                        // Output settings changed: let go of the device and open it the new way.
                        _reopen = false;
                        if (started) Freeze();
                        CloseDevice();
                        continue;
                    }
                    var client = _client!;
                    if (_volumeDirty)
                    {
                        _volumeDirty = false;
                        SetHardwareVolume();
                    }

                    long wanted;
                    lock (_lock) wanted = _seekSerial;
                    if (wanted != serial)
                    {
                        // Drop what Windows still holds of the old place.
                        if (started) client.Stop();
                        started = false;
                        client.Reset();
                        // and what the converter still holds of it
                        if (_resampler is DsdRemasterer dsd) _resampler = new DsdRemasterer(_sampleRate, dsd.DsdRate, _resampleFilter);
                        else if (_resampler is not null) _resampler = Resamplers.Create(_sampleRate, _outRate, _resampleFilter);
                        lock (_lock)
                        {
                            serial = _renderSerial = _seekSerial;
                            _baseSeconds = _seekTarget.TotalSeconds;
                            _consumed = 0;
                            _anchorFrames = _lastReported = 0;
                            _running = false;
                        }
                        ended = tailWritten = silenceAfterTail = false;
                    }

                    var play = _wantPlay && !ended;
                    if (play && !started)
                    {
                        client.Start();
                        started = true;
                        lock (_lock)
                        {
                            _anchorTicks = Stopwatch.GetTimestamp();
                            _running = true;
                        }
                    }
                    else if (!play && started)
                    {
                        client.Stop();
                        started = false;
                        Freeze();
                    }
                    if (!started)
                    {
                        _renderWake.WaitOne(100);
                        continue;
                    }

                    var woke = WaitHandle.WaitAny(handles, 200);
                    if (_closing) break;

                    var now = Stopwatch.GetTimestamp();
                    if (now >= nextDeviceCheck)
                    {
                        nextDeviceCheck = now + Stopwatch.Frequency;
                        if (WantedDeviceId() != _deviceId)
                        {
                            Freeze();
                            CloseDevice();
                            continue;
                        }
                    }

                    // Exclusive: the device hands over one whole buffer per signal, and only then.
                    if (_exclusiveOpen && woke != 0) continue;
                    var padding = _exclusiveOpen ? 0 : client.CurrentPadding;
                    var room = _bufferFrames - padding;
                    if (room <= 0) continue;

                    // How many of the song's frames fill the room (the resampler says, when there is one).
                    var wantedFrames = SongFramesFor(room);
                    int frames;
                    bool songOver;
                    lock (_lock)
                    {
                        var flowing = _ringSerial == serial && _seekSerial == serial;
                        frames = flowing ? Pull(buffer, Math.Min(wantedFrames, buffer.Length / 2)) : 0;
                        songOver = flowing && _endOfSong && _ringCount == 0;
                    }
                    if (frames > 0)
                    {
                        _decodeWake.Set();
                        if (_bitPerfect)
                        {
                            // the file's samples as they are: none of the chain. GoTune's volume
                            // still counts - never full blast because the volume was left to
                            // an amplifier that isn't there: below 100 % (without the device's
                            // own volume) they're turned down, gliding over the block
                            if (_dop)
                            {
                                // DSD words: the displays get silence, and nothing may touch the words
                                if (_silence.Length < frames * 2) _silence = new double[buffer.Length];
                                processor.Scope.Write(_silence.AsSpan(0, frames * 2));
                            }
                            else
                            {
                                processor.Scope.Write(buffer.AsSpan(0, frames * 2));
                            }
                            var level = _hardwareVolume || _dop ? 1.0 : _muted ? 0.0 : _volume;
                            if (level != 1 || _bitPerfectGain != 1)
                            {
                                var g = _bitPerfectGain;
                                var step = (level - g) / frames;
                                for (var i = 0; i < frames; i++)
                                {
                                    g += step;
                                    buffer[2 * i] *= g;
                                    buffer[2 * i + 1] *= g;
                                }
                                _bitPerfectGain = level;
                            }
                        }
                        else
                        {
                            processor.Process(buffer.AsSpan(0, frames * 2));
                        }
                        var written = Write(buffer, frames, room);
                        // the newest frames reach the speakers once the device's queue ahead of them
                        // has played (counted in the song's frames)
                        var queued = (padding + written) / _ratio;
                        processor.Scope.DelayFrames = (int)queued;
                        lock (_lock)
                        {
                            if (padding == 0 && _consumed > 0 && !_exclusiveOpen) Underruns++;
                            _consumed += frames;
                            if (_seekSerial == serial)
                            {
                                _anchorFrames = _consumed - queued;
                                _anchorTicks = Stopwatch.GetTimestamp();
                            }
                        }
                    }
                    else if (_exclusiveOpen && !songOver)
                    {
                        // nothing to play yet (just sought, or the decoder is behind): the device
                        // alone would play its old buffer again, so it gets silence
                        WriteSilence(room);
                    }
                    else if (songOver)
                    {
                        if (!tailWritten)
                        {
                            // The limiter and the converter still hold the song's last few
                            // milliseconds: push them out with silence.
                            tailWritten = true;
                            var held = _dop ? 0 : Math.Min(Math.Min((int)(room / _ratio), buffer.Length / 2),
                                processor.LatencyFrames + (_resampler?.LookaheadFrames ?? 0));
                            Array.Clear(buffer, 0, held * 2);
                            if (!_bitPerfect) processor.Process(buffer.AsSpan(0, held * 2));
                            Write(buffer, held, room);
                        }
                        else if (_exclusiveOpen && !silenceAfterTail)
                        {
                            // let the tail play out over one buffer of silence before stopping
                            silenceAfterTail = true;
                            WriteSilence(room);
                        }
                        else if (padding == 0)
                        {
                            client.Stop();
                            started = false;
                            ended = true;
                            lock (_lock)
                            {
                                _anchorFrames = _consumed;
                                _running = false;
                            }
                            if (!_closing) Ended?.Invoke();
                        }
                    }
                }
                catch (Exception) when (!_closing)
                {
                    // The device was taken away in the middle of a call.
                    Freeze();
                    CloseDevice();
                    started = false;
                    _renderWake.WaitOne(300);
                }
            }
        }
        finally
        {
            CloseDevice();
            if (mmcss != IntPtr.Zero) try { AvRevertMmThreadCharacteristics(mmcss); } catch { /* thread is ending anyway */ }
        }
    }

    /// <summary>DoP can't go out: nothing is played (DoP anywhere else is noise), and the owner
    /// opens the song again as PCM.</summary>
    private void RefuseDoP()
    {
        _wantPlay = false;
        Freeze();
        if (!_closing) RerouteNeeded?.Invoke();
    }

    /// <summary>Holds the position where it is (paused, or the device is gone).</summary>
    private void Freeze()
    {
        lock (_lock)
        {
            if (!_running) return;
            var frames = _anchorFrames + (Stopwatch.GetTimestamp() - _anchorTicks) * (double)_sampleRate / Stopwatch.Frequency;
            _anchorFrames = Math.Clamp(frames, 0, _consumed);
            _running = false;
        }
    }

    /// <summary>The song's processed stereo frames out to the device: resampled to its rate,
    /// spread over its speakers, in its sample format. Returns the device frames written.</summary>
    private int Write(double[] buffer, int frames, int room)
    {
        if (frames <= 0) return 0;
        ReadOnlySpan<double> stereo = buffer.AsSpan(0, frames * 2);
        var count = frames;
        if (_resampler is { } resampler)
        {
            if (_resampled.Length < room * 2) _resampled = new double[room * 2];
            count = resampler.Process(stereo, _resampled.AsSpan(0, room * 2));
            stereo = _resampled.AsSpan(0, count * 2);
        }
        if (count <= 0) return 0;
        var device = count;
        if (_exclusiveOpen && count < room)
        {
            // exclusive takes whole buffers: the rest is silence
            device = room;
        }
        var samples = count * _outChannels;
        if (_expanded.Length < samples) _expanded = new double[samples];
        _layout!.Expand(stereo, _expanded.AsSpan(0, samples));
        var bytes = samples * _writer!.BytesPerSample;
        if (_bytes.Length < bytes) _bytes = new byte[bytes];
        _writer.Write(_expanded.AsSpan(0, samples), _bytes.AsSpan(0, bytes));
        var allBytes = device * _outChannels * _writer.BytesPerSample;
        if (_bytes.Length < allBytes) Array.Resize(ref _bytes, allBytes);
        if (allBytes > bytes) Array.Clear(_bytes, bytes, allBytes - bytes);
        var target = _render!.GetBuffer(device);
        Marshal.Copy(_bytes, 0, target, allBytes);
        _render.ReleaseBuffer(device, AudioClientBufferFlags.None);
        return count;
    }

    private void WriteSilence(int frames)
    {
        if (frames <= 0) return;
        _render!.GetBuffer(frames);
        _render.ReleaseBuffer(frames, AudioClientBufferFlags.Silent);
    }

    /// <summary>How many of the song's frames make <paramref name="room"/> device frames. With a
    /// resampler that is its call (it keeps a few back between calls) - at most that many may
    /// follow in <see cref="Write"/>.</summary>
    private int SongFramesFor(int room) => _resampler is { } resampler ? resampler.InputFor(room) : room;

    /// <summary>Song frames enough to fill the device's whole buffer once.</summary>
    private double[] EnsureBuffer(double[] buffer)
    {
        var frames = (int)Math.Ceiling(_bufferFrames / _ratio) + 64 + (_resampler?.LookaheadFrames ?? 0);
        return buffer.Length >= frames * 2 ? buffer : new double[frames * 2];
    }

    /// <summary>Opens the playback device as <see cref="Output"/> says. Shared: float at the
    /// song's own rate, Windows converting to what the device runs at. Exclusive: the device
    /// alone, at the song's rate (or the chosen one) and the most bits it takes (or the chosen
    /// number) - falling back to a rate it does take, and to shared mode when the device can't
    /// be had (another program holds it, or it takes none of these formats).</summary>
    private void OpenDevice()
    {
        var output = _output;
        _enumerator ??= new MMDeviceEnumerator();
        _device = OutputDevices.Open(_enumerator, output.DeviceId) ?? throw new InvalidOperationException("No playback device.");
        _deviceId = _device.ID;
        var deviceChannels = SafeMixChannels(_device);
        var layout = ChannelLayout.Channels(output.Layout) <= deviceChannels ? output.Layout : SpeakerLayout.Stereo;
        var channels = ChannelLayout.Channels(layout);
        string? note = output.Layout != layout ? "이 장치에는 스피커가 그만큼 없어 스테레오로 재생 중" : null;
        if (output.DeviceId is not null && output.DeviceId != _deviceId) note = "고른 장치가 없어 기본 장치로 재생 중";
        var wantBitPerfect = output.Exclusive && output.BitPerfect || _dop;
        if (_dop && !output.Exclusive) throw new DopUnavailableException("not exclusive");
        _remastering = false;
        if (wantBitPerfect)
        {
            // the file's own channels: no spreading over more speakers
            layout = SpeakerLayout.Stereo;
            channels = 2;
            note = null;
        }
        _bitPerfect = false;

        if (output.Exclusive && !_dop && !wantBitPerfect && output.Remaster != DsdRemaster.Off)
        {
            // DSD remastering: the processed PCM goes out as DSD (DoP) - stereo
            var dsdRate = DsdRemasterer.DsdRateFor(_sampleRate, output.Remaster == DsdRemaster.Dsd128 ? 128 : 64);
            PcmFormat? dopFormat;
            using (var probe = _device.AudioClient) dopFormat = OutputDevices.DoPFormat(probe, dsdRate / 16);
            if (dopFormat is not null && TryOpenExclusive(dopFormat))
            {
                _exclusiveOpen = true;
                _remastering = true;
                var remaster = new DsdRemasterer(_sampleRate, dsdRate, output.Filter);
                Prepare(dopFormat.SampleRate, 2, SpeakerLayout.Stereo,
                    new SampleWriter(dopFormat.ValidBits, isFloat: false, dopFormat.BitsPerSample, dither: false), output.Filter, remaster);
                _outBits = dopFormat.ValidBits;
                _layoutName = LayoutName(SpeakerLayout.Stereo);
                if (layout != SpeakerLayout.Stereo) note = Join("DSD는 스테레오로 보냄", note);
                if (output.HardwareVolume && !TakeHardwareVolume())
                    note = note is null ? "장치에 하드웨어 볼륨이 없어 디지털 볼륨 사용" : Join(note, "장치에 하드웨어 볼륨이 없어 디지털 볼륨 사용");
                var name = $"DSD{dsdRate / (dsdRate % 44100 == 0 ? 44100 : 48000)}";
                _outputBase = $"DSD 리마스터링 · {name} ({dsdRate / 1e6:0.0}MHz, DoP {dopFormat.SampleRate / 1000.0:0.#}kHz)";
                _outputNote = note;
                OutputChanged?.Invoke();
                return;
            }
            note = Join($"장치가 DoP({dsdRate / 16 / 1000.0:0.#}kHz)를 받지 않아 리마스터링하지 않음", note);
            if (dopFormat is not null)
            {
                CloseDevice();
                _device = OutputDevices.Open(_enumerator, output.DeviceId) ?? throw new InvalidOperationException("No playback device.");
                _deviceId = _device.ID;
            }
        }

        if (output.Exclusive)
        {
            var format = PickExclusiveFormat(output, channels);
            if (_dop && (format is null || format.SampleRate != _sampleRate || format.ValidBits < 24))
                throw new DopUnavailableException("format");
            if (format is not null && TryOpenExclusive(format))
            {
                _exclusiveOpen = true;
                if (_dop && !TakeHardwareVolume())
                {
                    // DoP without the device's own volume would be full level: never
                    CloseDevice();
                    throw new DopUnavailableException("no hardware volume");
                }
                var exact = wantBitPerfect && !_converted && format.SampleRate == _sampleRate && (_sourceBits == 0 || format.ValidBits >= _sourceBits);
                Prepare(format.SampleRate, channels, layout, new SampleWriter(format.ValidBits, format.IsFloat, format.BitsPerSample, dither: !exact,
                    noiseShaping: format.SampleRate >= 88200, channels: channels), output.Filter);
                _outBits = format.ValidBits;
                _layoutName = LayoutName(layout);
                var rate = $"{format.ValidBits}비트 {format.SampleRate / 1000.0:0.#}kHz";
                if (wantBitPerfect && !exact)
                    note = format.SampleRate != _sampleRate
                        ? $"비트퍼펙트 아님: 장치가 {_sampleRate / 1000.0:0.#}kHz를 받지 않아 변환 중"
                        : $"비트퍼펙트 아님: 장치가 {_sourceBits}비트를 받지 않음";
                if (!_dop && output.HardwareVolume && !TakeHardwareVolume())
                {
                    const string noHardware = "장치에 하드웨어 볼륨이 없어 디지털 볼륨 사용";
                    note = note is null ? noHardware : Join(note, noHardware);
                }
                _outputBase = exact ? rate : $"독점 · {rate} · {LayoutName(layout)}";
                _outputNote = note;
                _bitPerfect = exact;
                OutputChanged?.Invoke();
                return;
            }
            if (_dop) throw new DopUnavailableException("device busy");
            note = Join(format is null ? "장치가 이 형식을 독점 모드로 받지 않아 공유 모드로 재생 중" : "다른 프로그램이 장치를 쓰고 있어 공유 모드로 재생 중", note);
            if (wantBitPerfect) note = "비트퍼펙트 아님: " + note;
            CloseDevice();
            _device = OutputDevices.Open(_enumerator, output.DeviceId) ?? throw new InvalidOperationException("No playback device.");
            _deviceId = _device.ID;
        }

        if (_dop) throw new DopUnavailableException("shared");
        _exclusiveOpen = false;
        // Upsampling in shared mode: GoTune converts to the rate Windows runs the device at, so
        // Windows' own converter has nothing left to do.
        var sharedRate = output.Upsampling != UpsampleMode.Off && SafeMixRate(_device) is > 0 and var mix ? mix : _sampleRate;
        _client = _device.AudioClient;
        _client.Initialize(AudioClientShareMode.Shared,
            AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
            DeviceBufferMilliseconds * 10_000L, 0, PcmFormat.CreateFloat(sharedRate, channels), Guid.Empty);
        _client.SetEventHandle(_audioEvent.SafeWaitHandle.DangerousGetHandle());
        _bufferFrames = _client.BufferSize;
        _render = _client.AudioRenderClient;
        Prepare(sharedRate, channels, layout, new SampleWriter(32, isFloat: true), output.Filter);
        _outBits = 0;
        _layoutName = LayoutName(layout);
        _outputBase = sharedRate != _sampleRate ? $"공유 · {sharedRate / 1000.0:0.#}kHz · {LayoutName(layout)}" : $"공유 · {LayoutName(layout)}";
        _outputNote = note;
        OutputChanged?.Invoke();
    }

    private static string Join(string first, string? second) => second is null ? first : $"{first} — {second}";

    private static string LayoutName(SpeakerLayout layout) => layout switch
    {
        SpeakerLayout.Quad => "4채널",
        SpeakerLayout.Surround51 => "5.1채널",
        SpeakerLayout.Surround71 => "7.1채널",
        _ => "스테레오",
    };

    // Each MMDevice.AudioClient is a new COM object of its own, let go of here.
    private static int SafeMixChannels(MMDevice device)
    {
        try { using var client = device.AudioClient; return client.MixFormat.Channels; }
        catch (COMException) { return 2; }
    }

    private static int SafeMixRate(MMDevice device)
    {
        try { using var client = device.AudioClient; return client.MixFormat.SampleRate; }
        catch (COMException) { return 0; }
    }

    /// <summary>Hands the volume to the device (exclusive mode), when it has a volume control of
    /// its own; what Windows had it at is kept to be put back. False when it has none.</summary>
    private bool TakeHardwareVolume()
    {
        try
        {
            var volume = _device!.AudioEndpointVolume;
            if (((int)volume.HardwareSupport & 1) == 0) return false; // ENDPOINT_HARDWARE_SUPPORT_VOLUME
            _endpointVolume = volume;
            // what to put back: Windows' level now - or the one a run that ended without putting
            // it back (a crash) wrote down, else that run's turned-down level would stick
            _heldDeviceId = _device.ID;
            (_savedLevel, _savedMute) = HeldVolume.Default.Take(_heldDeviceId, new(volume.MasterVolumeLevelScalar, volume.Mute));
            _hardwareVolume = true;
            ApplyVolume();
            SetHardwareVolume();
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return false;
        }
    }

    private void SetHardwareVolume()
    {
        if (!_hardwareVolume || _endpointVolume is not { } volume) return;
        try
        {
            // GoTune's 100 % is the level Windows had the device at before - turning hardware
            // volume on never makes it louder than it was
            volume.MasterVolumeLevelScalar = Math.Clamp(_savedLevel * _volume, 0f, 1f);
            volume.Mute = _muted;
        }
        catch (Exception)
        {
            // the device is going away
        }
        OutputChanged?.Invoke();
    }

    /// <summary>Puts Windows' volume for the device back the way it was.</summary>
    private void ReleaseHardwareVolume()
    {
        if (!_hardwareVolume) return;
        _hardwareVolume = false;
        var putBack = false;
        try
        {
            if (_endpointVolume is { } volume)
            {
                volume.MasterVolumeLevelScalar = _savedLevel;
                volume.Mute = _savedMute;
                putBack = true;
            }
        }
        catch (Exception)
        {
            // unplugged: the level stays written down, put back once the device is there again
        }
        if (_heldDeviceId is { } id) HeldVolume.Default.Release(id, putBack);
        _heldDeviceId = null;
        _endpointVolume = null;
        ApplyVolume(); // the samples carry the volume again
    }

    /// <summary>The exclusive format: the wanted rate (the song's, or the chosen one) if the
    /// device takes it, else the nearest it does (preferring the same family - 44.1/88.2/176.4 or
    /// 48/96/192 - so the conversion is gentler); the wanted bits, else the most it takes.</summary>
    private PcmFormat? PickExclusiveFormat(OutputSettings output, int channels)
    {
        using var probe = _device!.AudioClient;
        var bitPerfect = output.BitPerfect;
        var rate = output.ExclusiveSampleRate > 0 && !bitPerfect ? output.ExclusiveSampleRate : _sampleRate;
        // bit-perfect: 24 bits hold a 16- or 24-bit file exactly (and a decoded lossy one to its
        // last audible bit), then 32; 16 last (exact only for a 16-bit file)
        int[] bitsOrder = bitPerfect ? [24, 32, 16]
            : output.ExclusiveBits > 0 ? [output.ExclusiveBits, 32, 24, 16] : [32, 24, 16];
        if (_dop) return OutputDevices.DoPFormat(probe, _sampleRate);
        var rates = new List<int>();
        if (!bitPerfect && output.ExclusiveSampleRate == 0 && output.Upsampling != UpsampleMode.Off)
        {
            // whole multiples of the song's rate, the highest wanted first: the device's own
            // filter then has the least to do, and the conversion is exact (no fractional ratio)
            var most = output.Upsampling switch { UpsampleMode.Double => 2, UpsampleMode.Quadruple => 4, _ => 64 };
            for (var factor = most; factor >= 2; factor /= 2)
                if (_sampleRate * (long)factor <= OutputDevices.Rates[^1]) rates.Add(_sampleRate * factor);
        }
        rates.Add(rate);
        rates.AddRange(OutputDevices.Rates
            .OrderBy(r => r % 11025 == 0 == (rate % 11025 == 0) ? 0 : 1)
            .ThenBy(r => r < rate ? 1 : 0) // above first: no detail lost
            .ThenBy(r => Math.Abs(r - rate)));
        foreach (var r in rates.Distinct())
            foreach (var bits in bitsOrder.Distinct())
                if (OutputDevices.Format(probe, r, bits, channels) is { } format)
                    return format;
        return null;
    }

    /// <summary>Exclusive, event driven: the buffer is one device period, so it has to be a size
    /// the device lines up with - when it says "not aligned", the size it wants is taken.</summary>
    private bool TryOpenExclusive(PcmFormat format)
    {
        const int alignmentError = unchecked((int)0x88890019);
        var period = Math.Max(_device!.AudioClient.DefaultDevicePeriod, 30 * 10_000L); // at least 30 ms: rides out a GC pause
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var client = _device.AudioClient;
            try
            {
                client.Initialize(AudioClientShareMode.Exclusive, AudioClientStreamFlags.EventCallback, period, period, format, Guid.Empty);
                _client = client;
                _client.SetEventHandle(_audioEvent.SafeWaitHandle.DangerousGetHandle());
                _bufferFrames = _client.BufferSize;
                _render = _client.AudioRenderClient;
                return true;
            }
            catch (COMException ex) when (ex.HResult == alignmentError && attempt == 0)
            {
                var frames = client.BufferSize;
                client.Dispose();
                period = (long)Math.Round(10_000_000.0 * frames / format.SampleRate);
            }
            catch (COMException)
            {
                client.Dispose();
                return false;
            }
        }
        return false;
    }

    private void Prepare(int rate, int channels, SpeakerLayout layout, SampleWriter writer, ResampleFilter filter, IResampler? converter = null)
    {
        _outRate = rate;
        _outChannels = channels;
        _ratio = (double)rate / _sampleRate;
        _resampling = rate != _sampleRate;
        _layout = new ChannelLayout(layout, rate);
        _writer = writer;
        _resampler = converter ?? (rate == _sampleRate ? null : Resamplers.Create(_sampleRate, rate, filter));
        if (converter is not null) _resampling = false; // described as remastering instead
        _resampleFilter = filter;
        _resampleFilterName = _resampler is PolyphaseResampler ? Resamplers.FilterName(filter) : "기본 변환";
    }

    private void CloseDevice()
    {
        ReleaseHardwareVolume();
        try { _client?.Stop(); } catch { /* already invalid */ }
        try { _render?.Dispose(); } catch { /* already invalid */ }
        try { _client?.Dispose(); } catch { /* already invalid */ }
        try { _device?.Dispose(); } catch { /* already invalid */ }
        _render = null;
        _client = null;
        _device = null;
        _deviceId = null;
    }

    /// <summary>The device the sound should be on now: the chosen one while it is there, else
    /// Windows' default (null when there is none).</summary>
    private string? WantedDeviceId()
    {
        try
        {
            using var device = OutputDevices.Open(_enumerator!, _output.DeviceId);
            return device?.ID;
        }
        catch
        {
            return null; // no playback device at the moment
        }
    }

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}
