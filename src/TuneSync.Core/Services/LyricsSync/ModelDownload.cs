namespace TuneSync.Core.Services.LyricsSync;

/// <summary>Downloads a model file: to a .part file, renamed once it's complete and the size
/// it should be.</summary>
internal static class ModelDownload
{
    /// <param name="progress">0..1.</param>
    public static async Task ToFileAsync(string url, string path, long expectedBytes, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var part = path + ".part";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GoTunePlayer/1.0");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? expectedBytes;

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

        if (new FileInfo(part).Length != expectedBytes)
        {
            File.Delete(part);
            throw new IOException("모델 파일을 끝까지 받지 못했습니다. 다시 시도해 주세요.");
        }
        File.Move(part, path, overwrite: true);
    }
}
