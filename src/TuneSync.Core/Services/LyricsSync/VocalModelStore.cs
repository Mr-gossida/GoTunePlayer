namespace TuneSync.Core.Services.LyricsSync;

/// <summary>The model that takes the singing out of a song (UVR's MDX-Net "Voc FT"), kept next
/// to the speech recognition model and downloaded the same way, once.</summary>
public static class VocalModelStore
{
    public const string FileName = "UVR-MDX-NET-Voc_FT.onnx";
    public const long ExpectedBytes = 66_762_490;
    public const string DisplaySize = "64MB";
    private const string Url = "https://github.com/TRvlvr/model_repo/releases/download/all_public_uvr_models/" + FileName;

    public static string ModelPath => Path.Combine(WhisperModelStore.Folder, FileName);

    public static bool IsDownloaded => File.Exists(ModelPath) && new FileInfo(ModelPath).Length == ExpectedBytes;

    /// <summary>Downloads the model. Progress is 0..1.</summary>
    public static Task DownloadAsync(IProgress<double>? progress, CancellationToken ct) =>
        ModelDownload.ToFileAsync(Url, ModelPath, ExpectedBytes, progress, ct);

    /// <summary>Deletes the model when <paramref name="path"/> is it and it couldn't be read (see
    /// <see cref="WhisperModelStore.Discard"/>).</summary>
    internal static bool Discard(string path) => ModelDownload.Discard(path, ModelPath);
}
