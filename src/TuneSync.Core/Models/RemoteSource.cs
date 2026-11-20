namespace TuneSync.Core.Models;

public enum RemoteKind
{
    WebDav,
    Ftp,
    Ftps,
    Sftp,
    Smb,
}

/// <summary>A network location the library can read music from. The password is not stored here
/// (settings are a plain JSON file) but in Windows Credential Manager, keyed by <see cref="Id"/>.</summary>
public sealed class RemoteSource
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Name shown in the app, e.g. "거실 NAS".</summary>
    public string Name { get; set; } = "";

    public RemoteKind Kind { get; set; }

    /// <summary>WebDAV: the base URL (https://nas.local:5006/music). FTP/SFTP: the host name.
    /// Samba: the share (\\server\share).</summary>
    public string Address { get; set; } = "";

    /// <summary>0 = the protocol's default port.</summary>
    public int Port { get; set; }

    public string UserName { get; set; } = "";

    /// <summary>For self-signed certificates on home NAS boxes (WebDAV over https, FTPS): a certificate
    /// that fails the normal checks is accepted, but only the one trusted on the first connection
    /// (<see cref="CertificateThumbprint"/>). The name is older than that rule; settings files use it.</summary>
    public bool AcceptAnyCertificate { get; set; }

    /// <summary>WebDAV/FTPS with <see cref="AcceptAnyCertificate"/>: the SHA-256 (hex) of the
    /// certificate seen on the first connection. A certificate failing the normal checks is accepted
    /// only if it is this one; certificates that pass the checks need none. Null until then.</summary>
    public string? CertificateThumbprint { get; set; }

    /// <summary>SFTP: the server's host key trusted on the first connection - its SHA-256 as ssh prints
    /// it after "SHA256:". A server showing another key is refused until the user trusts it.</summary>
    public string? HostKeyFingerprint { get; set; }

    /// <summary>SFTP: that key's algorithm (ssh-ed25519, rsa-sha2-512...). It is asked for first, so a
    /// server with keys of several kinds keeps showing the trusted one.</summary>
    public string? HostKeyAlgorithm { get; set; }

    public int EffectivePort => Port > 0 ? Port : Kind switch
    {
        RemoteKind.Sftp => 22,
        RemoteKind.Ftp or RemoteKind.Ftps => 21,
        _ => 0,
    };

    public string KindLabel => Kind switch
    {
        RemoteKind.WebDav => "WebDAV",
        RemoteKind.Ftp => "FTP",
        RemoteKind.Ftps => "FTPS",
        RemoteKind.Sftp => "SFTP",
        RemoteKind.Smb => "Samba (SMB)",
        _ => Kind.ToString(),
    };
}

/// <summary>A file or folder on a remote source.</summary>
public sealed record RemoteEntry(string Name, string Path, bool IsDirectory, long Size, DateTime Modified);

/// <summary>Library paths for files on a remote source look like
/// <c>remote://{sourceId}/Music/KPop/song.flac</c>, so everything that keys off a path string
/// (the library, the queue, saved sessions) works unchanged.</summary>
public static class RemotePath
{
    public const string Scheme = "remote://";

    public static bool IsRemote(string? path) => path?.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) == true;

    public static string Build(string sourceId, string remotePath) =>
        Scheme + sourceId + "/" + remotePath.TrimStart('/');

    public static bool TryParse(string? path, out string sourceId, out string remotePath)
    {
        sourceId = remotePath = "";
        if (!IsRemote(path)) return false;
        var rest = path![Scheme.Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0) return false;
        sourceId = rest[..slash];
        remotePath = rest[slash..];
        return true;
    }

    /// <summary>"/Music/KPop" + "song.flac" with exactly one slash between.</summary>
    public static string Combine(string folder, string name) => folder.TrimEnd('/') + "/" + name.TrimStart('/');
}
