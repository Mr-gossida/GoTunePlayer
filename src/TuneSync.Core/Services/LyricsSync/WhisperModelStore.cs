namespace TuneSync.Core.Services.LyricsSync;

/// <summary>The speech recognition model, kept in %LocalAppData%\GoTune\models. It isn't
/// shipped with the app (190 MB): it's downloaded once, when the user first asks for it.</summary>
public static class WhisperModelStore
{
    public const string FileName = "ggml-small-q5_1.bin";
    public const long ExpectedBytes = 190_085_487;
    public const string DisplaySize = "190MB";
    private const string Url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + FileName;

    /// <summary>As Hugging Face publishes it for this file (its Git LFS object id).</summary>
    public const string Sha256 = "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb";

    private static string? _folder;

    /// <summary>The data folder's models folder unless set (tests).</summary>
    public static string Folder
    {
        get => _folder ?? AppDataFolder.Combine("models");
        set => _folder = value;
    }

    public static string ModelPath => Path.Combine(Folder, FileName);

    public static bool IsDownloaded => File.Exists(ModelPath) && new FileInfo(ModelPath).Length == ExpectedBytes;

    /// <summary>Downloads the model (to a .part file, renamed when complete). Progress is 0..1.</summary>
    public static Task DownloadAsync(IProgress<double>? progress, CancellationToken ct) =>
        ModelDownload.ToFileAsync(Url, ModelPath, ExpectedBytes, Sha256, progress, ct);

    /// <summary>Deletes the model when <paramref name="path"/> is it and it couldn't be read: only
    /// its size is known to be right, and without it the next sync offers to download it again.</summary>
    internal static bool Discard(string path) => ModelDownload.Discard(path, ModelPath);
}
