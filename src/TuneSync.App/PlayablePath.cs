using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TuneSync.App;

/// <summary>Windows Media (behind WPF's MediaPlayer) can't open a file on a network share whose
/// name contains '#' - it reports "file not found" (0x80070002), while the same name plays fine
/// locally. This hands the player a path it can open: the file's 8.3 short name (which never
/// contains '#'), or, if the share has short names disabled, a local temp copy.</summary>
public static class PlayablePath
{
    private static readonly string CacheDir = Path.Combine(Path.GetTempPath(), "TuneSync", "playback");
    private static string? _lastCopy;

    public static bool NeedsWorkaround(string path) => path.Contains('#') && path.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>Runs off the UI thread - the copy fallback reads the whole file over the network.</summary>
    public static string Resolve(string path)
    {
        if (!NeedsWorkaround(path)) return path;

        var buffer = new StringBuilder(1024);
        var length = GetShortPathName(path, buffer, buffer.Capacity);
        var shortPath = length > 0 && length < buffer.Capacity ? buffer.ToString() : null;
        if (shortPath is not null && !shortPath.Contains('#')) return shortPath;

        Directory.CreateDirectory(CacheDir);
        DeleteLastCopy();
        var copy = Path.Combine(CacheDir, Guid.NewGuid().ToString("N") + Path.GetExtension(path));
        File.Copy(path, copy);
        _lastCopy = copy;
        return copy;
    }

    /// <summary>A path Media Foundation can open for reading (waveform analysis): the short name
    /// for '#' paths on a share, otherwise the path itself. Never copies - analysis is optional,
    /// so if there's no short name it's simply skipped by the caller failing to open it.</summary>
    public static string ResolveForReading(string path)
    {
        if (!NeedsWorkaround(path)) return path;
        var buffer = new StringBuilder(1024);
        var length = GetShortPathName(path, buffer, buffer.Capacity);
        return length > 0 && length < buffer.Capacity ? buffer.ToString() : path;
    }

    public static void DeleteLastCopy()
    {
        if (_lastCopy is null) return;
        try { File.Delete(_lastCopy); } catch { /* still open or already gone - cleaned up next time */ }
        _lastCopy = null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, int bufferSize);
}
