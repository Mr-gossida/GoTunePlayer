using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using TuneSync.Core.Models;

namespace TuneSync.Core.Audio;

/// <summary>A playback device as the settings show it. The mix format is what Windows runs it
/// at in shared mode (설정 › 소리 › 장치 속성 › 고급 - not something an app can change).</summary>
public sealed record OutputDevice(string Id, string Name, bool IsDefault, int Channels, int SampleRate, int Bits);

/// <summary>A sample format a device takes in exclusive mode.</summary>
public readonly record struct DeviceFormat(int SampleRate, int Bits);

/// <summary>The playback devices, and what each can take.</summary>
public static class OutputDevices
{
    public static readonly int[] Rates = [44100, 48000, 88200, 96000, 176400, 192000, 352800, 384000, 705600, 768000];
    public static readonly int[] BitDepths = [16, 24, 32];

    public static IReadOnlyList<OutputDevice> List()
    {
        var list = new List<OutputDevice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultId = null;
            try
            {
                using var fallback = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                defaultId = fallback.ID;
            }
            catch (COMException)
            {
                // no playback device at all
            }
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    int channels = 2, rate = 48000, bits = 32;
                    if (SharedFormat(device) is { } shared)
                    {
                        (channels, rate, bits) = shared;
                    }
                    else
                    {
                        try
                        {
                            var mix = device.AudioClient.MixFormat;
                            channels = mix.Channels;
                            rate = mix.SampleRate;
                            bits = mix is WaveFormatExtensible { } x ? ValidBits(x) : mix.BitsPerSample;
                        }
                        catch (COMException)
                        {
                            // can't ask it now: shown with the usual format
                        }
                    }
                    list.Add(new OutputDevice(device.ID, device.FriendlyName, device.ID == defaultId, channels, rate, bits));
                }
            }
        }
        catch (COMException)
        {
            // audio service unavailable
        }
        return list;
    }

    /// <summary>PKEY_AudioEngine_DeviceFormat: the format Windows runs the device at in shared
    /// mode - what 소리 › 장치 속성 › 고급 › 기본 형식 sets. (The engine's mix format has the same rate
    /// and channels, but always 32-bit float, whatever the device's bits.)</summary>
    internal static readonly PropertyKey DeviceFormatKey = new(new Guid("f19f064d-082c-4e27-bc73-6882a1bb8e4c"), 0);

    /// <summary>The bits Windows runs the device at in shared mode (소리 › 장치 속성 › 고급 › 기본
    /// 형식); 0 when it can't be read.</summary>
    internal static int SharedBits(MMDevice device) => SharedFormat(device)?.Bits ?? 0;

    /// <summary>The device's shared-mode format as Windows has it set, or null when it can't be read.</summary>
    private static (int Channels, int Rate, int Bits)? SharedFormat(MMDevice device)
    {
        try
        {
            var store = device.Properties;
            if (!store.Contains(DeviceFormatKey) || store[DeviceFormatKey].Value is not byte[] blob || blob.Length < 16) return null;
            // WAVEFORMATEX: tag, channels, rate, bytes/s, block align, bits; EXTENSIBLE adds valid bits
            int channels = BitConverter.ToInt16(blob, 2), rate = BitConverter.ToInt32(blob, 4), bits = BitConverter.ToInt16(blob, 14);
            if (BitConverter.ToUInt16(blob, 0) == 0xFFFE && blob.Length >= 20 && BitConverter.ToInt16(blob, 18) is > 0 and var valid) bits = valid;
            return channels > 0 && rate > 0 && bits > 0 ? (channels, rate, bits) : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotImplementedException)
        {
            return null;
        }
    }

    /// <summary>The bits a format really carries (a 24-bit device often reports a 32-bit container).</summary>
    private static int ValidBits(WaveFormatExtensible format)
    {
        // wValidBitsPerSample sits right after the WAVEFORMATEX header
        var size = Marshal.SizeOf(format.GetType());
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(format, memory, false);
            var valid = Marshal.ReadInt16(memory, 18);
            return valid > 0 ? valid : format.BitsPerSample;
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    /// <summary>The formats the device takes in exclusive mode (stereo), lowest first. Empty
    /// when it can't be asked (gone, or held in exclusive mode - by another program, or by
    /// GoTune itself: see <see cref="KnownFormats"/>).</summary>
    public static IReadOnlyList<DeviceFormat> ExclusiveFormats(string? deviceId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = Open(enumerator, deviceId);
            return device is null ? [] : Probe(device);
        }
        catch (COMException)
        {
            return [];
        }
    }

    /// <summary>What each device said it takes in exclusive mode, the last time it could be
    /// asked, by its id. A device GoTune holds in exclusive mode can't be asked (it answers that
    /// it's in use) - and the player takes it as it opens the last song at start, so 설정 › 음향
    /// would otherwise have nothing to offer all session.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<DeviceFormat>> Known = new();

    /// <summary>The formats the device said it takes when it was last asked, or empty.</summary>
    public static IReadOnlyList<DeviceFormat> KnownFormats(string deviceId) => Known.TryGetValue(deviceId, out var formats) ? formats : [];

    /// <summary>Asks the device (once per run) before the player takes it in exclusive mode.</summary>
    internal static void RememberFormats(MMDevice device)
    {
        if (Known.ContainsKey(device.ID)) return;
        try
        {
            Probe(device);
        }
        catch (COMException)
        {
            // it can't be asked: nothing to remember
        }
    }

    private static IReadOnlyList<DeviceFormat> Probe(MMDevice device)
    {
        var formats = new List<DeviceFormat>();
        using var client = device.AudioClient;
        foreach (var rate in Rates)
            foreach (var bits in BitDepths)
                if (Supports(client, rate, bits, 2))
                    formats.Add(new DeviceFormat(rate, bits));
        if (formats.Count > 0) Known[device.ID] = formats;
        return formats;
    }

    /// <summary>The most channels the device has (from its shared-mode format).</summary>
    public static int Channels(string? deviceId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = Open(enumerator, deviceId);
            return device?.AudioClient.MixFormat.Channels ?? 2;
        }
        catch (COMException)
        {
            return 2;
        }
    }

    /// <summary>The device with this id when it is there, else Windows' default one.</summary>
    internal static MMDevice? Open(MMDeviceEnumerator enumerator, string? deviceId)
    {
        if (deviceId is not null)
        {
            try
            {
                var device = enumerator.GetDevice(deviceId);
                if (device.State == DeviceState.Active) return device;
                device.Dispose();
            }
            catch (COMException)
            {
                // unplugged or removed: the default one instead
            }
        }
        try
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch (COMException)
        {
            return null;
        }
    }

    internal static bool Supports(AudioClient client, int rate, int bits, int channels) => Format(client, rate, bits, channels) is not null;

    /// <summary>The exclusive format that carries DoP at <paramref name="rate"/>: stereo with at
    /// least 24 bits (24 packed or in 32, or 32), or null.</summary>
    internal static PcmFormat? DoPFormat(AudioClient client, int rate) =>
        Format(client, rate, 24, 2) ?? Format(client, rate, 32, 2);

    /// <summary>Whether DoP can go to the device at <paramref name="rate"/>: it takes such a
    /// format in exclusive mode, and it has a volume control of its own (DoP can't be turned
    /// down on the way, so without one it would play at full level).</summary>
    public static bool CanCarryDoP(string? deviceId, int rate)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = Open(enumerator, deviceId);
            if (device is null || ((int)device.AudioEndpointVolume.HardwareSupport & 1) == 0) return false;
            using var client = device.AudioClient;
            return DoPFormat(client, rate) is not null;
        }
        catch (COMException)
        {
            return false;
        }
    }

    /// <summary>The exclusive format the device takes for these bits, or null: 24 bits come
    /// packed in three bytes on some devices and in a four-byte container on most (HDMI and
    /// most sound cards), so both are tried.</summary>
    internal static PcmFormat? Format(AudioClient client, int rate, int bits, int channels)
    {
        int[] containers = bits == 24 ? [24, 32] : [bits];
        foreach (var container in containers)
        {
            var format = PcmFormat.Create(rate, bits, channels, container);
            try
            {
                if (client.IsFormatSupported(AudioClientShareMode.Exclusive, format)) return format;
            }
            catch (COMException)
            {
            }
        }
        return null;
    }
}

/// <summary>WAVEFORMATEXTENSIBLE with the right speaker positions for each layout, for integer
/// samples of 16, 24 or 32 bits or 32-bit float (NAudio's own sets the first N speakers, which
/// is wrong for quad and 7.1, and takes every 32-bit format for float).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal sealed class PcmFormat : WaveFormat
{
    private readonly short _validBits;
    // (bitsPerSample, from WaveFormat, is the container: 32 for 24 bits in four bytes)
    private readonly int _channelMask;
    private readonly Guid _subFormat;

    private static readonly Guid Pcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid Float = new("00000003-0000-0010-8000-00aa00389b71");

    private PcmFormat(int rate, int bits, int channels, bool isFloat, int container)
    {
        waveFormatTag = WaveFormatEncoding.Extensible;
        this.channels = (short)channels;
        sampleRate = rate;
        bitsPerSample = (short)container;
        blockAlign = (short)(channels * container / 8);
        averageBytesPerSecond = rate * blockAlign;
        extraSize = 22;
        _validBits = (short)bits;
        _channelMask = ChannelLayout.Mask(channels);
        _subFormat = isFloat ? Float : Pcm;
    }

    public bool IsFloat => _subFormat == Float;

    /// <summary>The bits that carry the sound (24 in a 32-bit container, say).</summary>
    public int ValidBits => _validBits;

    public static PcmFormat Create(int rate, int bits, int channels, int container = 0) =>
        new(rate, bits, channels, isFloat: false, container > 0 ? container : bits);

    public static PcmFormat CreateFloat(int rate, int channels) => new(rate, 32, channels, isFloat: true, 32);
}
