namespace TuneSync.Core.Services;

/// <summary>Where the app keeps its data (settings, library index, network cache, Whisper models):
/// %LocalAppData%\GoTune. Versions before the GoTune rename used %LocalAppData%\TuneSync; the
/// first time the path is asked for, that folder is moved over whole (a rename on the same
/// drive, so it is instant and nothing is re-downloaded or re-scanned).</summary>
public static class AppDataFolder
{
    private static readonly string Root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly Lazy<string> Resolved = new(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string NewPath { get; } = System.IO.Path.Combine(Root, "GoTune");
    public static string LegacyPath { get; } = System.IO.Path.Combine(Root, "TuneSync");

    /// <summary>The data folder (moved from the old location if needed).</summary>
    public static string Path => Resolved.Value;

    /// <summary>A file or folder inside the data folder.</summary>
    public static string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    private static string Resolve()
    {
        if (Directory.Exists(NewPath) || !Directory.Exists(LegacyPath)) return NewPath;
        try
        {
            Directory.Move(LegacyPath, NewPath);
            return NewPath;
        }
        catch
        {
            // Something holds a file in it (an older copy still running?): keep using the old
            // folder this time rather than starting with nothing; the move is tried next start.
            return LegacyPath;
        }
    }
}
