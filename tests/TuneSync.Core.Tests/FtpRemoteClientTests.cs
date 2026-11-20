using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using TuneSync.Core.Models;
using TuneSync.Core.Services.Remote;

namespace TuneSync.Core.Tests;

/// <summary>FTPS with "처음 받은 인증서를 신뢰": a self-signed certificate is trusted on first use, and
/// another one is refused before the user name and password are sent.</summary>
public class FtpRemoteClientTests
{
    /// <summary>Just enough of an FTPS server (explicit TLS) on the loopback address, with a self-signed
    /// certificate, to sign in and answer SIZE. It keeps the commands that reached it.</summary>
    private sealed class SelfSignedFtps : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public SelfSignedFtps()
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

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public ConcurrentQueue<string> Commands { get; } = new();

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
            {
                Stream stream = connection.GetStream();
                try
                {
                    await WriteLineAsync(stream, "220 ready");
                    while (await ReadLineAsync(stream) is { } line)
                    {
                        Commands.Enqueue(line);
                        var verb = line.Split(' ')[0].ToUpperInvariant();
                        if (verb == "AUTH")
                        {
                            await WriteLineAsync(stream, "234 go ahead");
                            var tls = new SslStream(stream);
                            await tls.AuthenticateAsServerAsync(Certificate);
                            stream = tls;
                            continue;
                        }
                        await WriteLineAsync(stream, verb switch
                        {
                            "USER" => "331 password please",
                            "PASS" => "230 signed in",
                            "FEAT" => "211-Features:\r\n SIZE\r\n UTF8\r\n211 End",
                            "SYST" => "215 UNIX Type: L8",
                            "PWD" => "257 \"/\"",
                            "SIZE" => "213 5",
                            "QUIT" => "221 bye",
                            _ => "200 ok",
                        });
                        if (verb == "QUIT") return;
                    }
                }
                catch
                {
                    // the client turned the certificate down, or hung up
                }
                finally
                {
                    await stream.DisposeAsync();
                }
            }
        }

        private static async Task WriteLineAsync(Stream stream, string line)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\r\n"));
            await stream.FlushAsync();
        }

        private static async Task<string?> ReadLineAsync(Stream stream)
        {
            var line = new List<byte>();
            var one = new byte[1];
            while (await stream.ReadAsync(one) == 1)
            {
                if (one[0] == '\n') return Encoding.UTF8.GetString([.. line]).TrimEnd('\r');
                line.Add(one[0]);
            }
            return null;
        }

        public void Dispose()
        {
            _listener.Stop();
            Certificate.Dispose();
        }
    }

    private static FtpRemoteClient ClientFor(SelfSignedFtps server, string? pinned = null) => new(new RemoteSource
    {
        Kind = RemoteKind.Ftps,
        Address = "127.0.0.1",
        Port = server.Port,
        UserName = "me",
        AcceptAnyCertificate = true,
        CertificateThumbprint = pinned,
    }, "secret");

    [Fact]
    public async Task SelfSignedCertificate_IsTrustedOnFirstUse_AndReportedOnceSignedIn()
    {
        using var server = new SelfSignedFtps();
        using var client = ClientFor(server);
        var learned = new List<ServerIdentity>();
        client.IdentityLearned += learned.Add;

        var size = await Task.Run(() => client.GetSize("/song.mp3"));

        Assert.Equal(5, size);
        Assert.Equal(server.Fingerprint, Assert.Single(learned).Fingerprint);
        Assert.Contains("PASS secret", server.Commands);
    }

    [Fact]
    public async Task AnotherCertificate_IsRefused_BeforeThePasswordIsSent()
    {
        using var server = new SelfSignedFtps();
        using var client = ClientFor(server, pinned: Convert.ToHexString(SHA256.HashData("the NAS's own certificate"u8)));
        var rejected = new List<ServerIdentity>();
        client.IdentityRejected += rejected.Add;

        var changed = await Assert.ThrowsAsync<ServerIdentityChangedException>(() => Task.Run(() => client.GetSize("/song.mp3")));

        Assert.Equal(server.Fingerprint, changed.Presented.Fingerprint);
        Assert.NotEmpty(rejected);
        Assert.DoesNotContain(server.Commands, command => command.StartsWith("USER", StringComparison.Ordinal)
                                                         || command.StartsWith("PASS", StringComparison.Ordinal));
    }
}
