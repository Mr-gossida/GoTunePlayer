using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;
using TuneSync.Core.Services;

namespace TuneSync.Core.Audio;

/// <summary>What Windows had a device's volume at before GoTune took it over (hardware volume),
/// written down in a small file while GoTune holds it. GoTune puts the volume back when it lets
/// go; a run that ended without doing so (a crash, a kill, a power cut) would leave the device
/// turned down or muted, and the next take would keep that as Windows' own level - so the level
/// written down is put back at the next start, or taken as the one to put back at the next take.</summary>
public sealed class HeldVolume(string file)
{
    public static HeldVolume Default { get; } = new(AppDataFolder.Combine("held-volume.json"));

    /// <summary>A device's volume: Windows' level (0..1) and whether it is muted.</summary>
    public readonly record struct Level(float Scalar, bool Mute);

    /// <summary>Allowance for a level reading back a hair off what was set.</summary>
    private const float Tolerance = 0.001f;

    private readonly object _gate = new();
    private readonly HashSet<string> _holding = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>GoTune takes the device's volume, which is at <paramref name="now"/>. Returns the
    /// level to put back - the one an earlier run wrote down and never put back, unless the device
    /// has been turned up past it since; else <paramref name="now"/> - and writes it down.</summary>
    public Level Take(string deviceId, Level now)
    {
        lock (_gate)
        {
            var held = Load();
            var before = held.TryGetValue(deviceId, out var left) && now.Scalar <= left.Scalar + Tolerance ? left : now;
            held[deviceId] = before;
            Save(held);
            _holding.Add(deviceId);
            return before;
        }
    }

    /// <summary>GoTune let go of the device's volume. <paramref name="putBack"/>: the level was put
    /// back (else the device went away first, and it stays written down for when it returns).</summary>
    public void Release(string deviceId, bool putBack)
    {
        lock (_gate)
        {
            _holding.Remove(deviceId);
            if (!putBack) return;
            var held = Load();
            if (held.Remove(deviceId)) Save(held);
        }
    }

    /// <summary>At start, off the UI thread: puts back the volume earlier runs left turned down or
    /// muted, unless the device has been turned up since. A device not there now waits for later.</summary>
    public void PutBackLeftovers()
    {
        lock (_gate)
        {
            var held = Load();
            if (held.Count == 0) return;
            var changed = false;
            try
            {
                using var devices = new MMDeviceEnumerator();
                foreach (var (id, before) in held.ToList())
                {
                    if (_holding.Contains(id)) continue; // taken again by this run, which puts it back
                    try
                    {
                        using var device = devices.GetDevice(id);
                        if (device.State != DeviceState.Active) continue;
                        var volume = device.AudioEndpointVolume;
                        if (volume.MasterVolumeLevelScalar <= before.Scalar + Tolerance)
                        {
                            volume.MasterVolumeLevelScalar = before.Scalar;
                            volume.Mute = before.Mute;
                        }
                        changed |= held.Remove(id);
                    }
                    catch (COMException)
                    {
                        // not there now
                    }
                }
            }
            catch (COMException)
            {
                // Windows' audio isn't running: tried again at the next start
            }
            if (changed) Save(held);
        }
    }

    private Dictionary<string, Level> Load()
    {
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<Dictionary<string, Level>>(File.ReadAllText(file)) is { } held)
                return new Dictionary<string, Level>(held, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // unreadable: nothing to put back
        }
        return new Dictionary<string, Level>(StringComparer.OrdinalIgnoreCase);
    }

    private void Save(Dictionary<string, Level> held)
    {
        try
        {
            if (held.Count == 0)
            {
                File.Delete(file);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(held));
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // not written: a crash now would leave the level as it is, as it did before this file
        }
    }
}
