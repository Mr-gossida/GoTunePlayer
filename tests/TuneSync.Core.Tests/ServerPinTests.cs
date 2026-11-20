using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TuneSync.Core.Services.Remote;

namespace TuneSync.Core.Tests;

/// <summary>Trust on first use for network locations: the SSH host key or self-signed certificate seen
/// first is pinned, and a different one is refused instead of being sent the password.</summary>
public class ServerPinTests
{
    private static X509Certificate2 SelfSigned(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    [Fact]
    public void HostKey_FirstOneSeenIsPinned_AndReportedOnceTheConnectionGotThrough()
    {
        var pin = new ServerPin(null, isCertificate: false);
        var learned = new List<ServerIdentity>();
        pin.Learned += learned.Add;
        var key = ServerIdentity.ForHostKey("ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og", "ssh-ed25519");

        Assert.True(pin.Check(key));
        Assert.Empty(learned); // not before the connection got all the way through (signed in)
        pin.Confirm();
        pin.Confirm();

        Assert.Equal(key, Assert.Single(learned));
        Assert.Equal(key.Fingerprint, pin.Pinned);
        Assert.True(pin.Check(key));
    }

    [Fact]
    public void HostKey_AnotherKeyIsRefused_AndSaidWhich()
    {
        var pin = new ServerPin("ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og", isCertificate: false);
        var rejected = new List<ServerIdentity>();
        pin.Rejected += rejected.Add;
        var learned = 0;
        pin.Learned += _ => learned++;
        var other = ServerIdentity.ForHostKey("Xq2v0n7m3QW1kz0yB6m3Cw5e1Jt9b0Pz4sLr8uVhYfA", "ssh-ed25519");

        Assert.False(pin.Check(other));
        // base64: a fingerprint differing only in case is another key
        Assert.False(pin.Check(ServerIdentity.ForHostKey("OHD8VZEXGWO6EZ8GSEJQ9WPAFGLFSOFLOTGGQCQO6OG", "ssh-ed25519")));
        pin.Confirm();

        Assert.Equal(2, rejected.Count);
        Assert.Equal(other, rejected[0]);
        Assert.Equal(0, learned);
        Assert.Equal("ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og", pin.Pinned);
    }

    [Fact]
    public void HostKey_SameKeyAgain_ClearsTheLastRefusal()
    {
        var pin = new ServerPin("AAAA", isCertificate: false);

        Assert.False(pin.Check(ServerIdentity.ForHostKey("BBBB", "ssh-ed25519")));
        Assert.Equal("BBBB", pin.LastRejected?.Fingerprint);
        Assert.True(pin.Check(ServerIdentity.ForHostKey("AAAA", "rsa-sha2-512")));
        Assert.Null(pin.LastRejected);
    }

    [Fact]
    public void Certificate_PassingTheNormalChecks_NeedsNoPin()
    {
        using var pinned = SelfSigned("nas");
        using var renewed = SelfSigned("nas"); // say, a renewed certificate a CA vouches for
        var pin = new ServerPin(ServerIdentity.ForCertificate(pinned).Fingerprint, isCertificate: true);
        var rejected = 0;
        pin.Rejected += _ => rejected++;

        Assert.True(pin.CheckCertificate(renewed, SslPolicyErrors.None, out var refused));

        Assert.Null(refused);
        Assert.Equal(0, rejected);
        Assert.Equal(ServerIdentity.ForCertificate(pinned).Fingerprint, pin.Pinned);
    }

    [Fact]
    public void Certificate_PassingTheNormalChecks_IsStillRememberedWhenNothingIsPinned()
    {
        using var valid = SelfSigned("nas.example.com");
        using var intruder = SelfSigned("nas.example.com"); // someone in between, self-signed
        var pin = new ServerPin(null, isCertificate: true);
        var learned = new List<ServerIdentity>();
        pin.Learned += learned.Add;

        Assert.True(pin.CheckCertificate(valid, SslPolicyErrors.None, out _));
        pin.Confirm();
        // otherwise this would be the first certificate failing the checks, and trusted as such
        Assert.False(pin.CheckCertificate(intruder, SslPolicyErrors.RemoteCertificateChainErrors, out var refused));

        Assert.Equal(ServerIdentity.ForCertificate(valid).Fingerprint, Assert.Single(learned).Fingerprint);
        Assert.Equal(ServerIdentity.ForCertificate(intruder).Fingerprint, refused?.Fingerprint);
    }

    [Fact]
    public void Certificate_FailingTheChecks_IsTrustedOnFirstUse_AndThenOnlyThatOne()
    {
        using var nas = SelfSigned("nas");
        using var other = SelfSigned("nas"); // same name, another key: what someone in between would show
        var pin = new ServerPin(null, isCertificate: true);
        var learned = new List<ServerIdentity>();
        pin.Learned += learned.Add;

        Assert.True(pin.CheckCertificate(nas, SslPolicyErrors.RemoteCertificateChainErrors, out _));
        pin.Confirm();
        Assert.True(pin.CheckCertificate(nas, SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors, out _));
        Assert.False(pin.CheckCertificate(other, SslPolicyErrors.RemoteCertificateChainErrors, out var refused));
        Assert.False(pin.CheckCertificate(null, SslPolicyErrors.RemoteCertificateNotAvailable, out _));

        Assert.Equal(ServerIdentity.ForCertificate(nas).Fingerprint, Assert.Single(learned).Fingerprint);
        Assert.NotNull(refused);
        Assert.Equal(ServerIdentity.ForCertificate(other).Fingerprint, refused.Fingerprint);
        Assert.Equal("CN=nas", refused.Subject);
    }

    [Fact]
    public void Certificate_PinnedHash_IsComparedIgnoringCase()
    {
        using var nas = SelfSigned("nas");
        var pin = new ServerPin(ServerIdentity.ForCertificate(nas).Fingerprint.ToLowerInvariant(), isCertificate: true);

        Assert.True(pin.CheckCertificate(nas, SslPolicyErrors.RemoteCertificateChainErrors, out _));
    }

    [Fact]
    public void Identity_ShowsFingerprintsTheWayPeopleCompareThem()
    {
        using var certificate = SelfSigned("nas");
        var identity = ServerIdentity.ForCertificate(certificate);
        var hash = Convert.ToHexString(SHA256.HashData(certificate.RawData));

        Assert.True(identity.IsCertificate);
        Assert.Equal(hash, identity.Fingerprint, ignoreCase: true);
        Assert.Equal(string.Join(' ', hash.Chunk(2).Select(p => new string(p))), identity.DisplayFingerprint, ignoreCase: true);
        Assert.Equal<DateTime?>(certificate.NotAfter, identity.ValidUntil);
        Assert.Equal("SHA256:abc", ServerIdentity.ForHostKey("abc", "ssh-ed25519").DisplayFingerprint);
    }

    [Fact]
    public void ChangedIdentity_IsAReadableIOException_NamingTheNewKey()
    {
        var changed = new ServerIdentityChangedException(ServerIdentity.ForHostKey("abc", "ssh-ed25519"));

        Assert.IsAssignableFrom<IOException>(changed);
        Assert.Contains("SSH 키", changed.Message);
        Assert.Contains("ssh-ed25519 SHA256:abc", changed.Message);
    }
}
