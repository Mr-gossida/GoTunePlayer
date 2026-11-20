using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TuneSync.Core.Services.Remote;

/// <summary>What a server proved who it is with: its SSH host key, or its TLS certificate.</summary>
/// <param name="Fingerprint">SSH: the key's SHA-256 in base64 without padding, as ssh prints it after
/// "SHA256:". TLS: the certificate's SHA-256 in hex.</param>
/// <param name="Algorithm">SSH: the key's algorithm (ssh-ed25519, rsa-sha2-512...).</param>
/// <param name="Subject">TLS: who the certificate was issued to.</param>
/// <param name="ValidUntil">TLS: when the certificate runs out.</param>
public sealed record ServerIdentity(bool IsCertificate, string Fingerprint, string? Algorithm = null,
    string? Subject = null, DateTime? ValidUntil = null)
{
    public static ServerIdentity ForHostKey(string fingerprintSha256, string algorithm) => new(false, fingerprintSha256, algorithm);

    public static ServerIdentity ForCertificate(X509Certificate certificate) => new(true,
        certificate.GetCertHashString(HashAlgorithmName.SHA256), Subject: certificate.Subject,
        ValidUntil: (certificate as X509Certificate2)?.NotAfter);

    /// <summary>The fingerprint the way people compare it: "SHA256:…" as ssh shows it, and a
    /// certificate's SHA-256 in pairs of hex digits as browsers show it.</summary>
    public string DisplayFingerprint => IsCertificate
        ? string.Join(' ', Fingerprint.Chunk(2).Select(pair => new string(pair)))
        : "SHA256:" + Fingerprint;
}

/// <summary>The server showed another key or certificate than the one trusted for it, so the
/// connection stopped before anything (the password) was sent. That is expected after reinstalling a
/// NAS or renewing its certificate - and it is also exactly what someone in between would look like,
/// so only the user can decide to trust the new one.</summary>
public sealed class ServerIdentityChangedException(ServerIdentity presented, Exception? inner = null)
    : IOException(Describe(presented), inner)
{
    /// <summary>What the server showed this time.</summary>
    public ServerIdentity Presented { get; } = presented;

    private static string Describe(ServerIdentity presented) => presented.IsCertificate
        ? $"서버 인증서가 이전에 신뢰한 인증서와 달라 연결하지 않았습니다. 새 인증서 SHA-256: {presented.DisplayFingerprint}"
        : $"서버의 SSH 키가 바뀌어 연결하지 않았습니다. 새 키: {presented.Algorithm} {presented.DisplayFingerprint}";
}

/// <summary>A client that checks who the server is (see <see cref="ServerPin"/>). The app keeps what
/// was learned with the location, and says why when a server no longer matches.</summary>
public interface IServerIdentityCheck
{
    /// <summary>The server's key or certificate was trusted on first use; raised once its first
    /// connection got all the way through, so it can be kept with the location.</summary>
    event Action<ServerIdentity>? IdentityLearned;

    /// <summary>A connection was refused because the server showed another key or certificate.</summary>
    event Action<ServerIdentity>? IdentityRejected;
}

/// <summary>Trust on first use for one location's server, the way ssh keeps known_hosts: the key or
/// certificate seen on the first connection is pinned, and from then on only that one is accepted.
/// Without it any server answering at the address - a fake Wi-Fi access point, a spoofed address -
/// would be sent the password. Thread-safe: a client's pooled connections share one.</summary>
public sealed class ServerPin(string? pinned, bool isCertificate)
{
    private readonly object _gate = new();

    // A certificate hash is hex, in either case; an SSH fingerprint is base64, where case matters.
    private readonly StringComparison _comparison = isCertificate ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private string? _pinned = string.IsNullOrWhiteSpace(pinned) ? null : pinned.Trim();
    private ServerIdentity? _learned;
    private ServerIdentity? _lastRejected;

    /// <summary>Pinned on first use - raised once the connection got all the way through
    /// (see <see cref="Confirm"/>).</summary>
    public event Action<ServerIdentity>? Learned;

    /// <summary>A server showed another key or certificate and was refused.</summary>
    public event Action<ServerIdentity>? Rejected;

    /// <summary>The fingerprint accepted now; null until the first connection.</summary>
    public string? Pinned
    {
        get { lock (_gate) return _pinned; }
    }

    /// <summary>What was refused last (null once one is accepted again): why a connection just failed.</summary>
    public ServerIdentity? LastRejected
    {
        get { lock (_gate) return _lastRejected; }
    }

    /// <summary>Whether to go on with a server showing <paramref name="presented"/>: yes for the pinned
    /// one, and for the first one seen when nothing is pinned yet (it becomes the pin).</summary>
    public bool Check(ServerIdentity presented)
    {
        lock (_gate)
        {
            if (_pinned is null)
            {
                _pinned = presented.Fingerprint;
                _learned = presented;
            }
            if (string.Equals(_pinned, presented.Fingerprint, _comparison))
            {
                _lastRejected = null;
                return true;
            }
            _lastRejected = presented;
        }
        Rejected?.Invoke(presented);
        return false;
    }

    /// <summary>TLS: a certificate that passes the normal checks is accepted as before, whatever is
    /// pinned. One that fails them (a NAS's self-signed certificate) must be the pinned one - or, with
    /// none pinned yet, becomes it. Never "any certificate" and never "only the name is wrong": anyone
    /// can get a valid certificate for a name of their own.</summary>
    /// <param name="refused">The certificate refused for not being the pinned one.</param>
    public bool CheckCertificate(X509Certificate? certificate, SslPolicyErrors errors, out ServerIdentity? refused)
    {
        refused = null;
        if (certificate is null) return errors == SslPolicyErrors.None;
        var presented = ServerIdentity.ForCertificate(certificate);
        if (errors == SslPolicyErrors.None)
        {
            Remember(presented);
            return true;
        }
        if (Check(presented)) return true;
        refused = presented;
        return false;
    }

    /// <summary>A certificate that passed the normal checks: with nothing pinned yet it becomes the pin
    /// all the same. Otherwise, for a server whose certificate is valid, the first one failing the checks -
    /// someone in between with a self-signed one - would be what gets trusted "on first use".</summary>
    private void Remember(ServerIdentity presented)
    {
        lock (_gate)
        {
            _lastRejected = null;
            if (_pinned is not null) return;
            _pinned = presented.Fingerprint;
            _learned = presented;
        }
    }

    /// <summary>Called when a connection got all the way through (signed in, answered): a key or
    /// certificate pinned on the way is reported then, once, to be kept with the location.</summary>
    public void Confirm()
    {
        ServerIdentity? learned;
        lock (_gate)
        {
            learned = _learned;
            _learned = null;
        }
        if (learned is not null) Learned?.Invoke(learned);
    }
}
