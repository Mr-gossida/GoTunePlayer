using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

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
/// the equalizer and hands them to Windows (WASAPI, shared mode) - so a slider moved in the
/// equalizer is heard a fraction of a second later, and a slow disk or network never stalls the
/// sound device. One player per song; <see cref="Close"/> releases the file.
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

    public TonePlayer(string path, ToneConfig config)
    {
        _path = path;
        _config = config;
    }

    public TonePlayer(IProgressiveSource source, ToneConfig config)
    {
        _source = source;
        _config = config;
    }

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
            var buffer = new float[_bufferFrames * 2];
            WaitHandle[] handles = [_audioEvent, _renderWake];
            long serial = 0, nextDeviceCheck = 0;
            bool started = false, ended = false, tailWritten = false;
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
                        if (buffer.Length < _bufferFrames * 2) buffer = new float[_bufferFrames * 2];
                        started = false;
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
                        ended = tailWritten = false;
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

                    WaitHandle.WaitAny(handles, 200);
                    if (_closing) break;

                    var now = Stopwatch.GetTimestamp();
                    if (now >= nextDeviceCheck)
                    {
                        nextDeviceCheck = now + Stopwatch.Frequency;
                        if (DefaultDeviceId() != _deviceId)
                        {
                            Freeze();
                            CloseDevice();
                            continue;
                        }
                    }

                    var padding = client.CurrentPadding;
                    var room = _bufferFrames - padding;
                    if (room <= 0) continue;

                    int frames;
                    bool songOver;
                    lock (_lock)
                    {
                        var flowing = _ringSerial == serial && _seekSerial == serial;
                        frames = flowing ? Pull(buffer, room) : 0;
                        songOver = flowing && _endOfSong && _ringCount == 0;
                    }
                    if (frames > 0)
                    {
                        _decodeWake.Set();
                        processor.Process(buffer.AsSpan(0, frames * 2));
                        Write(buffer, frames);
                        // the newest frames reach the speakers once the device's queue ahead of them has played
                        processor.Scope.DelayFrames = padding + frames;
                        lock (_lock)
                        {
                            if (padding == 0 && _consumed > 0) Underruns++;
                            _consumed += frames;
                            if (_seekSerial == serial)
                            {
                                _anchorFrames = _consumed - (padding + frames);
                                _anchorTicks = Stopwatch.GetTimestamp();
                            }
                        }
                    }
                    else if (songOver)
                    {
                        if (!tailWritten)
                        {
                            // The limiter still holds the song's last few milliseconds: push them out.
                            tailWritten = true;
                            var tail = Math.Min(room, processor.LatencyFrames);
                            Array.Clear(buffer, 0, tail * 2);
                            processor.Process(buffer.AsSpan(0, tail * 2));
                            Write(buffer, tail);
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

    private void Write(float[] buffer, int frames)
    {
        if (frames <= 0) return;
        var target = _render!.GetBuffer(frames);
        Marshal.Copy(buffer, 0, target, frames * 2);
        _render.ReleaseBuffer(frames, AudioClientBufferFlags.None);
    }

    /// <summary>Opens the default playback device for two-channel float sound at the song's own
    /// rate; Windows converts to whatever the device runs at.</summary>
    private void OpenDevice()
    {
        _enumerator ??= new MMDeviceEnumerator();
        _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        _deviceId = _device.ID;
        _client = _device.AudioClient;
        _client.Initialize(AudioClientShareMode.Shared,
            AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
            DeviceBufferMilliseconds * 10_000L, 0, WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, 2), Guid.Empty);
        _client.SetEventHandle(_audioEvent.SafeWaitHandle.DangerousGetHandle());
        _bufferFrames = _client.BufferSize;
        _render = _client.AudioRenderClient;
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

    private string? DefaultDeviceId()
    {
        try
        {
            using var device = _enumerator!.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.ID;
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
