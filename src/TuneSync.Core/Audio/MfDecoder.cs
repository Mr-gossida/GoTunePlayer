using System.Runtime.InteropServices;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.MediaFoundation;

namespace TuneSync.Core.Audio;

/// <summary>Decodes a song to floating-point samples through Windows' own codecs (Media
/// Foundation: MP3, AAC/M4A, FLAC, WMA, WAV...), from a file or an http address. Unlike NAudio's
/// ready-made reader it lands exactly on the time asked for: Windows starts decoding at the
/// nearest earlier point it can (for FLAC that can be seconds before), and the samples up to the
/// wanted time are dropped here - otherwise the lyrics would run ahead of the sound after a seek.
/// Created, used and disposed on one thread.</summary>
public sealed class MfDecoder : IAudioDecoder
{
    private const int FirstAudioStream = MediaFoundationInterop.MF_SOURCE_READER_FIRST_AUDIO_STREAM;
    private const int InvalidMediaType = unchecked((int)0xC00D36B4);

    private IMFSourceReader? _reader;
    private bool _float;
    private int _bytesPerSample;

    // Samples decoded but not handed out yet.
    private float[] _pending = new float[16384];
    private int _pendingAt, _pendingCount;
    private byte[] _raw = [];

    private long _seekTarget = -1; // 100 ns units; -1 when the next sample isn't the first after a seek
    private long _skipFrames;

    public int SampleRate { get; private set; }
    public int Channels { get; private set; }

    public bool IsConverted => false;
    public bool IsDoP => false;

    /// <summary>Zero when Windows can't tell (some streams).</summary>
    public TimeSpan Duration { get; }

    public MfDecoder(string pathOrUrl)
    {
        MediaFoundationApi.Startup();
        try
        {
            _reader = Open(pathOrUrl, asFloat: true);
        }
        catch (COMException)
        {
            // A decoder that only hands out whole numbers: converted here instead.
            _reader = Open(pathOrUrl, asFloat: false);
        }
        Duration = ReadDuration(_reader);
    }

    private IMFSourceReader Open(string pathOrUrl, bool asFloat)
    {
        MediaFoundationInterop.MFCreateSourceReaderFromURL(pathOrUrl, null!, out var reader);
        try
        {
            reader.SetStreamSelection(MediaFoundationInterop.MF_SOURCE_READER_ALL_STREAMS, false);
            reader.SetStreamSelection(FirstAudioStream, true);

            reader.GetCurrentMediaType(FirstAudioStream, out var nativeType);
            var native = new MediaType(nativeType);
            ReadSource(native);
            var wanted = new MediaType
            {
                MajorType = MediaTypes.MFMediaType_Audio,
                SubType = asFloat ? AudioSubtypes.MFAudioFormat_Float : AudioSubtypes.MFAudioFormat_PCM,
                // Stated outright: left open, mono and low-rate files come out wrong on Windows 10.
                ChannelCount = native.ChannelCount,
                SampleRate = native.SampleRate,
            };
            try
            {
                reader.SetCurrentMediaType(FirstAudioStream, IntPtr.Zero, wanted.MediaFoundationObject);
            }
            catch (COMException ex) when (ex.HResult == InvalidMediaType && native.SubType == AudioSubtypes.MFAudioFormat_AAC && native.ChannelCount == 1)
            {
                // HE-AAC is announced at half its real rate and channel count.
                wanted.SampleRate = native.SampleRate * 2;
                wanted.ChannelCount = native.ChannelCount * 2;
                reader.SetCurrentMediaType(FirstAudioStream, IntPtr.Zero, wanted.MediaFoundationObject);
            }
            finally
            {
                Marshal.ReleaseComObject(nativeType);
            }
            Marshal.ReleaseComObject(wanted.MediaFoundationObject);

            ReadFormat(reader, asFloat);
            return reader;
        }
        catch
        {
            Marshal.ReleaseComObject(reader);
            throw;
        }
    }

    /// <summary>Bits per sample in the file itself, for lossless formats (PCM, FLAC, ALAC); 0
    /// for lossy ones (MP3, AAC...), whose sound only exists once decoded.</summary>
    public int SourceBits { get; private set; }

    private static readonly Guid[] Lossless =
    [
        new("00000001-0000-0010-8000-00aa00389b71"), // PCM
        new("00000003-0000-0010-8000-00aa00389b71"), // float
        new("0000f1ac-0000-0010-8000-00aa00389b71"), // FLAC
        new("63616c61-0000-0010-8000-00aa00389b71"), // ALAC
    ];

    private void ReadSource(MediaType native)
    {
        try
        {
            SourceBits = Lossless.Contains(native.SubType) ? native.BitsPerSample : 0;
        }
        catch (COMException)
        {
            SourceBits = 0; // not stated
        }
    }

    private void ReadFormat(IMFSourceReader reader, bool asFloat)
    {
        reader.GetCurrentMediaType(FirstAudioStream, out var type);
        try
        {
            var format = new MediaType(type);
            var bits = format.BitsPerSample;
            if (format.SampleRate <= 0 || format.ChannelCount <= 0 || (asFloat ? bits != 32 : bits is not (8 or 16 or 24 or 32)))
                throw new NotSupportedException($"Unsupported audio format ({format.SampleRate} Hz, {format.ChannelCount} ch, {bits} bit).");
            if (SampleRate != 0 && (SampleRate != format.SampleRate || Channels != format.ChannelCount))
                throw new NotSupportedException("The audio format changes in the middle of the song.");
            SampleRate = format.SampleRate;
            Channels = format.ChannelCount;
            _float = asFloat;
            _bytesPerSample = bits / 8;
        }
        finally
        {
            Marshal.ReleaseComObject(type);
        }
    }

    private static TimeSpan ReadDuration(IMFSourceReader reader)
    {
        var variant = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariant>());
        try
        {
            var result = reader.GetPresentationAttribute(MediaFoundationInterop.MF_SOURCE_READER_MEDIASOURCE,
                MediaFoundationAttributes.MF_PD_DURATION, variant);
            if (result != 0) return TimeSpan.Zero;
            var value = Marshal.PtrToStructure<PropVariant>(variant).Value;
            PropVariant.Clear(variant);
            return TimeSpan.FromTicks(Math.Max(0, System.Convert.ToInt64(value)));
        }
        catch
        {
            return TimeSpan.Zero;
        }
        finally
        {
            Marshal.FreeHGlobal(variant);
        }
    }

    /// <summary>The next read starts exactly at <paramref name="time"/>.</summary>
    public void Seek(TimeSpan time)
    {
        var reader = _reader ?? throw new ObjectDisposedException(nameof(MfDecoder));
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;
        var position = PropVariant.FromLong(time.Ticks);
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariant>());
        try
        {
            Marshal.StructureToPtr(position, pointer, false);
            reader.SetCurrentPosition(Guid.Empty, pointer);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
        _pendingAt = _pendingCount = 0;
        _skipFrames = 0;
        _seekTarget = time.Ticks;
    }

    /// <summary>Fills <paramref name="target"/> with whole frames (all channels interleaved).
    /// Returns the number of frames; fewer than asked for only at the end of the song.</summary>
    public int Read(Span<float> target)
    {
        var frames = target.Length / Channels;
        var written = 0;
        while (written < frames)
        {
            if (_pendingCount == 0 && !ReadSample()) break;

            var available = _pendingCount / Channels;
            if (_skipFrames > 0)
            {
                var skip = (int)Math.Min(_skipFrames, available);
                _skipFrames -= skip;
                _pendingAt += skip * Channels;
                _pendingCount -= skip * Channels;
                continue;
            }
            var take = Math.Min(available, frames - written);
            _pending.AsSpan(_pendingAt, take * Channels).CopyTo(target[(written * Channels)..]);
            _pendingAt += take * Channels;
            _pendingCount -= take * Channels;
            written += take;
        }
        return written;
    }

    /// <summary>Decodes the next packet into <see cref="_pending"/>. False at the end of the song.</summary>
    private bool ReadSample()
    {
        var reader = _reader ?? throw new ObjectDisposedException(nameof(MfDecoder));
        while (true)
        {
            reader.ReadSample(FirstAudioStream, 0, out _, out var flags, out var timestamp, out var sample);
            try
            {
                if ((flags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ERROR) != 0)
                    throw new InvalidOperationException("Media Foundation could not read the audio.");
                if ((flags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0)
                    ReadFormat(reader, _float);
                if ((flags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM) != 0) return false;
                if (sample is null) continue; // a gap in the stream

                if (_seekTarget >= 0)
                {
                    // Decoding started this much before the time asked for.
                    var early = _seekTarget - (long)timestamp;
                    _skipFrames = early > 0 ? Math.Min(early, TimeSpan.TicksPerMinute) * SampleRate / TimeSpan.TicksPerSecond : 0;
                    _seekTarget = -1;
                }

                sample.ConvertToContiguousBuffer(out var buffer);
                try
                {
                    buffer.Lock(out var data, out _, out var length);
                    try
                    {
                        Convert(data, length);
                    }
                    finally
                    {
                        buffer.Unlock();
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(buffer);
                }
                if (_pendingCount > 0) return true;
            }
            finally
            {
                if (sample is not null) Marshal.ReleaseComObject(sample);
            }
        }
    }

    private void Convert(IntPtr data, int length)
    {
        var count = length / _bytesPerSample;
        count -= count % Channels;
        if (_pending.Length < count) _pending = new float[count];
        _pendingAt = 0;
        _pendingCount = count;
        if (_float)
        {
            Marshal.Copy(data, _pending, 0, count);
            return;
        }

        if (_raw.Length < length) _raw = new byte[length];
        Marshal.Copy(data, _raw, 0, length);
        switch (_bytesPerSample)
        {
            case 1:
                for (var i = 0; i < count; i++) _pending[i] = (_raw[i] - 128) / 128f;
                break;
            case 2:
                for (var i = 0; i < count; i++) _pending[i] = BitConverter.ToInt16(_raw, i * 2) / 32768f;
                break;
            case 3:
                for (var i = 0; i < count; i++)
                    _pending[i] = ((_raw[i * 3] << 8 | _raw[i * 3 + 1] << 16 | _raw[i * 3 + 2] << 24) >> 8) / 8388608f;
                break;
            default:
                for (var i = 0; i < count; i++) _pending[i] = BitConverter.ToInt32(_raw, i * 4) / 2147483648f;
                break;
        }
    }

    public void Dispose()
    {
        if (_reader is null) return;
        Marshal.ReleaseComObject(_reader);
        _reader = null;
    }
}
