using System.Text;
using TuneSync.Core.Models;

namespace TuneSync.Core.Services;

/// <summary>Lyrics kept next to the song: "노래.lrc" (or "노래.txt") in the song's folder, as many
/// players and lyrics tools save them. Nothing leaves the computer. Old Korean files are often
/// in EUC-KR (CP949) rather than UTF-8; both are read.</summary>
public static class LocalLyricsSource
{
    public const string SourceName = "폴더 파일";

    private static readonly string[] Extensions = [".lrc", ".txt"];

    /// <summary>Files bigger than this aren't lyrics.</summary>
    private const long MaxBytes = 512 * 1024;

    static LocalLyricsSource() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>The lyrics files beside <paramref name="audioPath"/> (a file on this computer;
    /// none for a network song), synced ones first.</summary>
    public static List<LyricsCandidate> Find(string audioPath, string title = "", string artist = "")
    {
        var list = new List<LyricsCandidate>();
        if (Models.RemotePath.IsRemote(audioPath)) return list;
        try
        {
            var folder = Path.GetDirectoryName(audioPath);
            var name = Path.GetFileNameWithoutExtension(audioPath);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return list;
            foreach (var extension in Extensions)
            {
                var path = Path.Combine(folder, name + extension);
                if (!File.Exists(path) || new FileInfo(path).Length is 0 or > MaxBytes) continue;
                var text = Read(File.ReadAllBytes(path)).Trim();
                if (text.Length == 0) continue;
                var synced = LrcParser.IsSynced(text);
                // a .txt beside the song is only taken when it reads as lyrics (several lines)
                if (!synced && extension == ".txt" && text.Split('\n').Length < 4) continue;
                list.Add(new LyricsCandidate
                {
                    Source = SourceName,
                    Title = string.IsNullOrWhiteSpace(title) ? name : title,
                    Artist = artist,
                    Lyrics = text,
                    IsSynced = synced,
                    FileName = Path.GetFileName(path),
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // unreadable: as if it weren't there
        }
        return [.. list.OrderByDescending(c => c.IsSynced)];
    }

    /// <summary>The text of a lyrics file: UTF-8 (with or without a BOM) or UTF-16 when it reads as
    /// that, else CP949 - the encoding older Korean lyrics files and tools used.</summary>
    public static string Read(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, start, bytes.Length - start);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(949).GetString(bytes);
        }
    }
}
