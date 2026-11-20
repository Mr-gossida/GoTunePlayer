using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;
using TuneSync.Core.Models;
using TuneSync.Core.Services.Remote;

namespace TuneSync.Core.Tests;

/// <summary>Folder listings from a WebDAV server are read with limits, so a broken or hostile server
/// can't fill memory or have entities expanded; and with "처음 받은 인증서를 신뢰" a self-signed
/// certificate is trusted on first use, and another one is refused before the password goes out.</summary>
public class WebDavRemoteClientTests
{
    private sealed class Answering(Func<HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(answer());
    }

    /// <summary>Spaces without end, sent without a length (as a chunked answer would be).</summary>
    private sealed class Endless : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            buffer.AsSpan(offset, count).Fill((byte)' ');
            return count;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static WebDavRemoteClient ClientAnswering(Func<HttpResponseMessage> answer) =>
        new(new RemoteSource { Kind = RemoteKind.WebDav, Address = "https://nas.local/dav" }, "", new Answering(answer));

    [Fact]
    public async Task Listing_WithADtd_IsRefused()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <!DOCTYPE d:multistatus [<!ENTITY name "song">]>
            <d:multistatus xmlns:d="DAV:"><d:response><d:href>/dav/&name;.mp3</d:href></d:response></d:multistatus>
            """;
        using var client = ClientAnswering(() => new HttpResponseMessage((HttpStatusCode)207) { Content = new StringContent(xml) });

        await Assert.ThrowsAsync<XmlException>(() => client.ListAsync("/", CancellationToken.None));
    }

    [Fact]
    public async Task Listing_AnnouncedLargerThanAnyRealOne_IsRefusedBeforeReading()
    {
        using var client = ClientAnswering(() =>
        {
            var content = new ByteArrayContent([]);
            content.Headers.ContentLength = 100L * 1024 * 1024;
            return new HttpResponseMessage((HttpStatusCode)207) { Content = content };
        });

        await Assert.ThrowsAsync<HttpRequestException>(() => client.ListAsync("/", CancellationToken.None));
    }

    [Fact]
    public async Task Listing_ThatNeverEnds_IsCutOff()
    {
        using var client = ClientAnswering(() => new HttpResponseMessage((HttpStatusCode)207) { Content = new StreamContent(new Endless()) });

        await Assert.ThrowsAsync<HttpRequestException>(() => client.ListAsync("/", CancellationToken.None));
    }

    // --- the server's certificate: a WebDAV server on the loopback address with a self-signed one ---

    /// <summary>What a home NAS - or someone in between - looks like to the client: HTTPS on the
    /// loopback address with a self-signed certificate. It answers each PROPFIND with one song and
    /// keeps what reached it, to tell whether the user name and password were sent.</summary>
    private sealed class SelfSignedNas : IDisposable
    {
        private const string Listing = """<?xml version="1.0" encoding="utf-8"?><d:multistatus xmlns:d="DAV:"><d:response><d:href>/dav/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response><d:response><d:href>/dav/song.mp3</d:href><d:propstat><d:prop><d:resourcetype/><d:getcontentlength>5</d:getcontentlength></d:prop></d:propstat></d:response></d:multistatus>""";

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public SelfSignedNas()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=nas.local", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            // Windows' TLS takes the key from a key store, not from memory: hence the round trip through a PFX
            Certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
            _listener.Start();
            _ = ServeAsync();
        }

        public X509Certificate2 Certificate { get; }

        public string Fingerprint => ServerIdentity.ForCertificate(Certificate).Fingerprint;

        public string Address => $"https://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/dav";

        /// <summary>The request line and headers of each request that got through.</summary>
        public ConcurrentQueue<string> Requests { get; } = new();

        private async Task ServeAsync()
        {
            while (true)
            {
                TcpClient connection;
                try
                {
                    connection = await _listener.AcceptTcpClientAsync();
                }
                catch
                {
                    return; // stopped
                }
                _ = AnswerAsync(connection);
            }
        }

        private async Task AnswerAsync(TcpClient connection)
        {
            using (connection)
            using (var tls = new SslStream(connection.GetStream()))
            {
                try
                {
                    await tls.AuthenticateAsServerAsync(Certificate);
                    if (await ReadRequestAsync(tls) is not { } head) return;
                    Requests.Enqueue(head);
                    var body = Encoding.UTF8.GetBytes(Listing);
                    await tls.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 207 Multi-Status\r\nContent-Type: application/xml; charset=utf-8\r\n"
                                                                + $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
                    await tls.WriteAsync(body);
                    await tls.FlushAsync();
                }
                catch
                {
                    // the client turned the certificate down, or hung up
                }
            }
        }

        /// <summary>The request line and headers; the body (the PROPFIND query) is read past.</summary>
        private static async Task<string?> ReadRequestAsync(Stream stream)
        {
            var buffer = new byte[64 * 1024];
            var length = 0;
            int end;
            while ((end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8)) < 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length));
                if (read == 0) return null;
                length += read;
            }
            var head = Encoding.ASCII.GetString(buffer, 0, end);
            var bodyLength = head.Split("\r\n")
                .Where(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                .Select(line => int.Parse(line["Content-Length:".Length..].Trim()))
                .FirstOrDefault();
            for (var received = length - end - 4; received < bodyLength;)
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0) break;
                received += read;
            }
            return head;
        }

        public void Dispose()
        {
            _listener.Stop();
            Certificate.Dispose();
        }
    }

    private static WebDavRemoteClient ClientFor(SelfSignedNas nas, bool trustFirstCertificate, string? pinned = null) => new(new RemoteSource
    {
        Kind = RemoteKind.WebDav,
        Address = nas.Address,
        UserName = "me",
        AcceptAnyCertificate = trustFirstCertificate,
        CertificateThumbprint = pinned,
    }, "secret");

    private static CancellationToken Soon() => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    [Fact]
    public async Task SelfSignedCertificate_IsTrustedOnFirstUse_AndReportedOnceTheServerAnswered()
    {
        using var nas = new SelfSignedNas();
        using var client = ClientFor(nas, trustFirstCertificate: true);
        var learned = new List<ServerIdentity>();
        client.IdentityLearned += learned.Add;

        var entries = await client.ListAsync("/", Soon());

        Assert.Equal("song.mp3", Assert.Single(entries).Name);
        Assert.Equal(nas.Fingerprint, Assert.Single(learned).Fingerprint);
        Assert.Contains("Authorization: Basic ", Assert.Single(nas.Requests));
    }

    [Fact]
    public async Task PinnedCertificate_IsAcceptedAgain()
    {
        using var nas = new SelfSignedNas();
        using var client = ClientFor(nas, trustFirstCertificate: true, pinned: nas.Fingerprint.ToLowerInvariant());
        var learned = 0;
        client.IdentityLearned += _ => learned++;

        Assert.Single(await client.ListAsync("/", Soon()));
        Assert.Equal(0, learned);
    }

    [Fact]
    public async Task AnotherCertificate_IsRefused_BeforeThePasswordIsSent()
    {
        using var nas = new SelfSignedNas();
        using var client = ClientFor(nas, trustFirstCertificate: true, pinned: Convert.ToHexString(SHA256.HashData("the NAS's own certificate"u8)));
        var rejected = new List<ServerIdentity>();
        client.IdentityRejected += rejected.Add;

        var changed = await Assert.ThrowsAsync<ServerIdentityChangedException>(() => client.ListAsync("/", Soon()));

        Assert.Equal(nas.Fingerprint, changed.Presented.Fingerprint);
        Assert.Equal("CN=nas.local", changed.Presented.Subject);
        Assert.Contains(changed.Presented.DisplayFingerprint, changed.Message);
        Assert.NotEmpty(rejected);
        Assert.Empty(nas.Requests); // no request - so no user name and password - reached it
    }

    [Fact]
    public async Task WithoutTheOption_ASelfSignedCertificateIsRefusedAsBefore()
    {
        using var nas = new SelfSignedNas();
        using var client = ClientFor(nas, trustFirstCertificate: false);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.ListAsync("/", Soon()));

        Assert.IsAssignableFrom<AuthenticationException>(error.InnerException);
        Assert.Empty(nas.Requests);
    }
}
