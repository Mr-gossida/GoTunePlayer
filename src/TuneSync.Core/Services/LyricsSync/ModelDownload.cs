using System.Security.Cryptography;

namespace TuneSync.Core.Services.LyricsSync;

/// <summary>Downloads a model file over HTTPS to a .part file next to it, moved into place only
/// once it has arrived whole, exactly the size it should be and with the SHA-256 it should have.
/// A server that announces or sends another size or other bytes (a file changed or replaced where
/// it's published, or swapped on the way), a broken connection, a full disk or cancelling leave
/// nothing behind, so a half-written or different file is never loaded - the models are run by
/// native code (whisper.cpp, ONNX Runtime) that trusts what it reads.</summary>
public static class ModelDownload
{
    /// <param name="sha256">The file's SHA-256, in hex.</param>
    /// <param name="progress">0..1.</param>
    public static async Task ToFileAsync(string url, string path, long expectedBytes, string sha256, IProgress<double>? progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GoTunePlayer/1.0");
        await ToFileAsync(http, url, path, expectedBytes, sha256, progress, ct);
    }

    /// <summary>The same with a given client (tests).</summary>
    public static async Task ToFileAsync(HttpClient http, string url, string path, long expectedBytes, string sha256, IProgress<double>? progress, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Models are only downloaded over HTTPS.", nameof(url));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var part = path + ".part";
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            // The download is sent on to a CDN. .NET doesn't follow a redirect from HTTPS down to
            // HTTP, but where it ended up is checked all the same.
            if (response.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps) throw NotTheModel();
            // Another size: the file was changed where it's published (or this isn't it). Said
            // now rather than after the whole download.
            if (response.Content.Headers.ContentLength is { } length && length != expectedBytes) throw NotTheModel();

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    done += read;
                    // More than the model: stop at once (a server that never stops mustn't fill the disk).
                    if (done > expectedBytes) throw NotTheModel();
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    progress?.Report((double)done / expectedBytes);
                }
                if (done != expectedBytes) throw new IOException("모델 파일을 끝까지 받지 못했습니다. 다시 시도해 주세요.");
            }
            // The right size isn't enough: the same number of other bytes is another file.
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(sha256, StringComparison.OrdinalIgnoreCase)) throw NotTheModel();
            File.Move(part, path, overwrite: true);
        }
        catch
        {
            // The next try would overwrite it, but until then it takes up to 190 MB.
            try { File.Delete(part); } catch { /* still open elsewhere: left for the next try */ }
            throw;
        }
    }

    private static IOException NotTheModel() =>
        new("모델 파일이 예상과 다릅니다. 배포처에서 파일이 바뀌었거나 손상되었을 수 있습니다.");

    /// <summary>Deletes <paramref name="ownPath"/> (a model that turned out unreadable) when
    /// <paramref name="path"/> is that file; anything else - a model given by path, as the
    /// benchmark does - is left alone. True when it was deleted.</summary>
    internal static bool Discard(string path, string ownPath)
    {
        if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(ownPath), StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            File.Delete(ownPath);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
