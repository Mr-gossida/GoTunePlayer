namespace TuneSync.Core.Services.LyricsSync;

/// <summary>The speech recognition model, kept in %LocalAppData%\TuneSync\models. It isn't
/// shipped with the app (190 MB): it's downloaded once, when the user first asks for it.</summary>
public static class WhisperModelStore
{
    public const string FileName = "ggml-small-q5_1.bin";
    public const long ExpectedBytes = 190_085_487;
    public const string DisplaySize = "190MB";
    private const string Url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + FileName;

    public static string Folder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TuneSync", "models");

    public static string ModelPath => Path.Combine(Folder, FileName);

    public static bool IsDownloaded => File.Exists(ModelPath) && new FileInfo(ModelPath).Length == ExpectedBytes;

    /// <summary>Downloads the model (to a .part file, renamed when complete). Progress is 0..1.</summary>
    public static async Task DownloadAsync(IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        var part = ModelPath + ".part";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TuneSync/0.1");
        using var response = await http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? ExpectedBytes;

        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                progress?.Report((double)done / total);
            }
        }

        if (new FileInfo(part).Length != ExpectedBytes)
        {
            File.Delete(part);
            throw new IOException("모델 파일을 끝까지 받지 못했습니다. 다시 시도해 주세요.");
        }
        File.Move(part, ModelPath, overwrite: true);
    }
}
