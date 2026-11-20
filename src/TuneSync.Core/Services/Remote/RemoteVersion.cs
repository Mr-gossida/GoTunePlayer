using System.Collections.Concurrent;
using System.Text.Json;
using TuneSync.Core.Models;

namespace TuneSync.Core.Services.Remote;

/// <summary>Which version of a network file something is, told by the size and date the server
/// lists for the file. <see cref="Modified"/> is <c>default</c> when the server gave no date; a
/// change that kept the size (a tag edited in place) can't be told apart without one.</summary>
public readonly record struct RemoteVersion(long Size, DateTime Modified)
{
    public static RemoteVersion Of(RemoteEntry entry) => new(entry.Size, entry.Modified);

    public bool HasDate => Modified != default;

    /// <summary>Certainly the same version: the same size and the same date, the date being known.</summary>
    public bool IsSameAs(RemoteVersion other) => HasDate && Size == other.Size && Modified == other.Modified;

    /// <summary>Certainly another version: another size, or another date where both are known.</summary>
    public bool DiffersFrom(RemoteVersion other) =>
        Size != other.Size || (HasDate && other.HasDate && Modified != other.Modified);

    /// <summary>The version a local copy of <paramref name="length"/> bytes holds, given the one it
    /// was recorded as: none (not known) once it has another size - edited here, or cut short.</summary>
    public static RemoteVersion? Held(RemoteVersion? recorded, long length) =>
        recorded is { } version && version.Size == length ? version : null;

    /// <summary>Whether a local copy is certainly of another version than the one the server was
    /// last seen to hold (<paramref name="seen"/>, from a folder listing - no round trip).</summary>
    public static bool IsOutdated(RemoteVersion? recorded, long length, RemoteVersion? seen) =>
        seen is { Size: > 0 } server && (Held(recorded, length) ?? new RemoteVersion(length, default)).DiffersFrom(server);

    /// <summary>The version a finished download of <paramref name="length"/> bytes holds.
    /// <paramref name="expected"/> is the version known before it began (looked up, or last listed:
    /// older than the download, so it can only make the copy seem older than it is - which costs a
    /// download before an edit, never an edit made on an old copy); <paramref name="announced"/> is
    /// the size the server gave when it began (0 or less: none). None (not known) when the file
    /// didn't come as long as announced - cut short, or changed on the server meanwhile.</summary>
    public static RemoteVersion? OfDownload(RemoteVersion? expected, long announced, long length)
    {
        if (announced > 0 && length != announced) return null;
        if (expected is { } version && version.Size == length) return version;
        // Only the size is confirmed (by the server's own word), not which version it is.
        return announced > 0 ? new RemoteVersion(length, default) : null;
    }
}

/// <summary>Which version of the server's file each local copy (by file name) holds, kept in a
/// small JSON file so it survives a restart. A copy without an entry is of an unknown version.</summary>
public sealed class CopyVersions
{
    private readonly string _file;
    private readonly ConcurrentDictionary<string, RemoteVersion> _versions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _saveGate = new();

    public CopyVersions(string file)
    {
        _file = file;
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<Dictionary<string, Stored>>(File.ReadAllText(file)) is { } stored)
                foreach (var (copy, version) in stored)
                    _versions[copy] = new RemoteVersion(version.Size, new DateTime(version.Ticks));
        }
        catch
        {
            _versions.Clear(); // unreadable: every copy counts as unknown (fetched again before an edit)
        }
    }

    public RemoteVersion? Get(string copy) => _versions.TryGetValue(copy, out var version) ? version : null;

    /// <summary>Records the version a copy holds now - or, with null, that it isn't known (the copy
    /// is being replaced, or is gone) - and saves.</summary>
    public void Set(string copy, RemoteVersion? version)
    {
        if (version is { } known) _versions[copy] = known;
        else if (!_versions.TryRemove(copy, out _)) return;
        Save();
    }

    /// <summary>Forgets the versions of copies that were removed (saved once).</summary>
    public void Forget(IEnumerable<string> copies)
    {
        var changed = false;
        foreach (var copy in copies) changed |= _versions.TryRemove(copy, out _);
        if (changed) Save();
    }

    /// <summary>Forgets the versions of every copy but <paramref name="copies"/> (the ones still there).</summary>
    public void Retain(IEnumerable<string> copies)
    {
        var kept = new HashSet<string>(copies, StringComparer.OrdinalIgnoreCase);
        Forget(_versions.Keys.Where(copy => !kept.Contains(copy)).ToList());
    }

    private void Save()
    {
        lock (_saveGate)
        {
            try
            {
                var stored = _versions.ToDictionary(p => p.Key, p => new Stored(p.Value.Size, p.Value.Modified.Ticks));
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                var temp = _file + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(stored));
                File.Move(temp, _file, overwrite: true);
            }
            catch
            {
                // Not saved: after a restart such a copy counts as unknown, or as the older version
                // recorded before - never as newer than it is, so an edit fetches it again first.
            }
        }
    }

    /// <summary>The date as ticks: read back exactly, whatever time zone it was given in.</summary>
    private sealed record Stored(long Size, long Ticks);
}
