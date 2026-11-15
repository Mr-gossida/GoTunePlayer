namespace TuneSync.Core.Services;

/// <summary>Where the app keeps its data (settings, library index, network cache, Whisper models):
/// %LocalAppData%\GoTune. Versions before the GoTune rename used %LocalAppData%\TuneSync; the
/// first time the path is asked for, that folder is moved over whole (a rename on the same
/// drive, so it is instant and nothing is re-downloaded or re-scanned).</summary>
public static class AppDataFolder
{
    private static readonly string Root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string NewPath { get; } = System.IO.Path.Combine(Root, "GoTune");
    public static string LegacyPath { get; } = System.IO.Path.Combine(Root, "TuneSync");
    private static readonly Lazy<string> Resolved = new(() => Resolve(NewPath, LegacyPath), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The data folder (moved from the old location if needed).</summary>
    public static string Path => Resolved.Value;

    /// <summary>A file or folder inside the data folder.</summary>
    public static string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>The folder to use, given where it should be and where older versions kept it:
    /// the old one is moved over when only it exists. Takes the two paths so tests can run it
    /// on folders of their own.</summary>
    public static string Resolve(string newPath, string legacyPath)
    {
        if (Directory.Exists(newPath) || !Directory.Exists(legacyPath)) return newPath;
        try
        {
            Directory.Move(legacyPath, newPath);
            return newPath;
        }
        catch
        {
            // Something holds a file in it (an older copy still running?): keep using the old
            // folder this time rather than starting with nothing; the move is tried next start.
            return legacyPath;
        }
    }
}
