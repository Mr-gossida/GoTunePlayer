namespace TuneSync.Core.Models;

/// <summary>One folder registered as a music source, the way a media player's library is built
/// from several watched folders rather than a single active one.</summary>
public sealed class LibraryFolderEntry
{
    public required string Path { get; set; }
    public bool IncludeSubfolders { get; set; } = true;

    /// <summary>True when <see cref="Path"/> is a single audio file registered directly
    /// (picked via "음악 파일 추가"), rather than a folder to scan.</summary>
    public bool IsFile { get; set; } = false;

    /// <summary>Set when the folder lives on a <see cref="RemoteSource"/>. For WebDAV/FTP/SFTP,
    /// <see cref="Path"/> is the folder on that server ("/Music/KPop"); Samba folders stay plain
    /// UNC paths and carry this only so the share can be signed in to at startup.</summary>
    public string? RemoteSourceId { get; set; }

    /// <summary>Read through a remote client (not the file system).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsRemote => RemoteSourceId is not null && !Path.StartsWith(@"\\", StringComparison.Ordinal);
}
