using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
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
}

/// <summary>Plays one song through the sound chain (<see cref="ToneProcessor"/>): a decoding
/// thread fills a short buffer of raw samples, and an output thread takes them from there, runs
/// the equalizer and hands them to Windows (WASAPI) - so a slider moved in the equalizer is heard
/// a fraction of a second later, and a slow disk or network never stalls the sound device. Where
/// the sound goes is <see cref="Output"/>: a chosen device or Windows' default, stereo or spread
/// over more speakers, shared with other programs or (exclusive mode) sent to the device alone at
/// a sample rate and bit depth it takes - the song's own rate when it can, resampled when not.
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
    private int _sourceBits;
    private float[]? _resampleInput;
    private int _resampleOffset;
    private ChannelLayout? _layout;
    private SampleWriter? _writer;
    private WdlResampler? _resampler;
    private float[] _expanded = [];
    private float[] _resampled = [];
    private byte[] _bytes = [];

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
            if (_bitPerfect)
            {
                var source = _sourceBits > 0 ? $"원본 {_sourceBits}비트 그대로" : "디코딩한 소리 그대로";
                return Join($"비트퍼펙트 · {device} · {source}", _outputNote);
            }
            // what changes the sound on the way, so "독점" isn't mistaken for untouched
            var applied = new List<string>();
            if (_resampling) applied.Add($"곡 {_sampleRate / 1000.0:0.#}kHz에서 변환");
            if (!_exclusiveOpen) applied.Add("Windows 믹서 거침");
            if (!ReferenceEquals(_config, ToneConfig.Neutral)) applied.Add("이퀄라이저 적용");
            if (_muted) applied.Add("음소거");
            else if (_volume < 1) applied.Add($"볼륨 {Math.Round(_volume * 100)}%");
            var text = applied.Count > 0 ? $"{device} ({string.Join(" · ", applied)})" : device;
            return Join(text, _outputNote);
        }
    }

    /// <summary>The samples reach the device exactly as the file holds them (bit-perfect mode,
    /// and the device took the song's own rate).</summary>
    public bool IsBitPerfect => _bitPerfect;

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
        if (_processor is { } processor) processor.Volume = _muted ? 0 : _volume;
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
        MfDecoder? decoder = null;
        var fromUrl = false;
        try
        {
            try
            {
                var local = _path ?? _source!.CompletedPath;
                fromUrl = local is null;
                decoder = new MfDecoder(local ?? _source!.Url);
                if (decoder.Channels > 8) throw new NotSupportedException("Too many channels.");
            }
            catch (Exception ex)
            {
                Fail(ex, _source is { IsBroken: true } ? TonePlayerFailure.Source : TonePlayerFailure.Unsupported);
                return;
            }

            _sampleRate = decoder.SampleRate;
            _sourceBits = decoder.SourceBits;
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
    private bool SeekWhileDownloading(ref MfDecoder decoder, ref bool fromUrl, TimeSpan target, long serial)
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

    private int Pull(float[] target, int frames)
    {
        frames = Math.Min(frames, _ringCount);
        var first = Math.Min(frames, _ringCapacity - _ringStart);
        Array.Copy(_ring, _ringStart * 2, target, 0, first * 2);
        Array.Copy(_ring, 0, target, first * 2, (frames - first) * 2);
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

                    long wanted;
                    lock (_lock) wanted = _seekSerial;
                    if (wanted != serial)
                    {
                        // Drop what Windows still holds of the old place.
                        if (started) client.Stop();
                        started = false;
                        client.Reset();
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
                            // the file's samples as they are: none of the chain, no volume (only mute)
                            if (_muted) Array.Clear(buffer, 0, frames * 2);
                            processor.Scope.Write(buffer.AsSpan(0, frames * 2));
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
                            // The limiter still holds the song's last few milliseconds: push them out.
                            tailWritten = true;
                            var tail = Math.Min(Math.Min((int)(room / _ratio), buffer.Length / 2), processor.LatencyFrames);
                            Array.Clear(buffer, 0, tail * 2);
                            processor.Process(buffer.AsSpan(0, tail * 2));
                            Write(buffer, tail, room);
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
    private int Write(float[] buffer, int frames, int room)
    {
        if (frames <= 0) return 0;
        ReadOnlySpan<float> stereo = buffer.AsSpan(0, frames * 2);
        var count = frames;
        if (_resampler is { } resampler)
        {
            // the frames go where SongFramesFor was told they would
            stereo.CopyTo(_resampleInput.AsSpan(_resampleOffset, frames * 2));
            if (_resampled.Length < room * 2) _resampled = new float[room * 2];
            count = resampler.ResampleOut(_resampled, 0, frames, room, 2);
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
        if (_expanded.Length < samples) _expanded = new float[samples];
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
    /// resampler that is its call (it keeps a few back between calls), and it is then told where
    /// they will be put - so exactly that many must follow in <see cref="Write"/>.</summary>
    private int SongFramesFor(int room)
    {
        if (_resampler is not { } resampler) return room;
        var needed = resampler.ResamplePrepare(room, 2, out var input, out var offset);
        _resampleInput = input;
        _resampleOffset = offset;
        return needed;
    }

    /// <summary>Song frames enough to fill the device's whole buffer once.</summary>
    private float[] EnsureBuffer(float[] buffer)
    {
        var frames = (int)Math.Ceiling(_bufferFrames / _ratio) + 64;
        return buffer.Length >= frames * 2 ? buffer : new float[frames * 2];
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
        var wantBitPerfect = output.Exclusive && output.BitPerfect;
        if (wantBitPerfect)
        {
            // the file's own channels: no spreading over more speakers
            layout = SpeakerLayout.Stereo;
            channels = 2;
            note = null;
        }
        _bitPerfect = false;

        if (output.Exclusive)
        {
            var format = PickExclusiveFormat(output, channels);
            if (format is not null && TryOpenExclusive(format))
            {
                _exclusiveOpen = true;
                var exact = wantBitPerfect && format.SampleRate == _sampleRate && (_sourceBits == 0 || format.ValidBits >= _sourceBits);
                Prepare(format.SampleRate, channels, layout, new SampleWriter(format.ValidBits, format.IsFloat, format.BitsPerSample, dither: !exact));
                var rate = $"{format.ValidBits}비트 {format.SampleRate / 1000.0:0.#}kHz";
                if (wantBitPerfect && !exact)
                    note = format.SampleRate != _sampleRate
                        ? $"비트퍼펙트 아님: 장치가 {_sampleRate / 1000.0:0.#}kHz를 받지 않아 변환 중"
                        : $"비트퍼펙트 아님: 장치가 {_sourceBits}비트를 받지 않음";
                _outputBase = exact ? rate : $"독점 · {rate} · {LayoutName(layout)}";
                _outputNote = note;
                _bitPerfect = exact;
                OutputChanged?.Invoke();
                return;
            }
            note = Join(format is null ? "장치가 이 형식을 독점 모드로 받지 않아 공유 모드로 재생 중" : "다른 프로그램이 장치를 쓰고 있어 공유 모드로 재생 중", note);
            if (wantBitPerfect) note = "비트퍼펙트 아님: " + note;
            CloseDevice();
            _device = OutputDevices.Open(_enumerator, output.DeviceId) ?? throw new InvalidOperationException("No playback device.");
            _deviceId = _device.ID;
        }

        _exclusiveOpen = false;
        _client = _device.AudioClient;
        _client.Initialize(AudioClientShareMode.Shared,
            AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
            DeviceBufferMilliseconds * 10_000L, 0, PcmFormat.CreateFloat(_sampleRate, channels), Guid.Empty);
        _client.SetEventHandle(_audioEvent.SafeWaitHandle.DangerousGetHandle());
        _bufferFrames = _client.BufferSize;
        _render = _client.AudioRenderClient;
        Prepare(_sampleRate, channels, layout, new SampleWriter(32, isFloat: true));
        _outputBase = $"공유 · {LayoutName(layout)}";
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

    private static int SafeMixChannels(MMDevice device)
    {
        try { return device.AudioClient.MixFormat.Channels; }
        catch (COMException) { return 2; }
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
        var rates = new List<int> { rate };
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

    private void Prepare(int rate, int channels, SpeakerLayout layout, SampleWriter writer)
    {
        _outRate = rate;
        _outChannels = channels;
        _ratio = (double)rate / _sampleRate;
        _resampling = rate != _sampleRate;
        _layout = new ChannelLayout(layout, rate, _output.SubCrossoverHz);
        _writer = writer;
        if (rate == _sampleRate)
        {
            _resampler = null;
            return;
        }
        _resampler = new WdlResampler();
        _resampler.SetMode(true, 2, false);
        _resampler.SetFilterParms();
        _resampler.SetFeedMode(false); // asked for so many out, it says how many in
        _resampler.SetRates(_sampleRate, rate);
    }

    private void CloseDevice()
    {
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
