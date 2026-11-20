using System.Collections.Concurrent;
using Renci.SshNet;
using TuneSync.Core.Models;

namespace TuneSync.Core.Services.Remote;

/// <summary>SFTP (SSH file transfer). A few pooled connections let the scanner, the player and
/// edits work at the same time.</summary>
public sealed class SftpRemoteClient : IRemoteClient, IServerIdentityCheck
{
    private const int MaxConnections = 4;
    private readonly RemoteSource _source;
    private readonly string _password;
    private readonly ServerPin _hostKey;
    private readonly string? _trustedKeyType;
    private readonly ConcurrentBag<SftpClient> _idle = [];
    private readonly SemaphoreSlim _slots = new(MaxConnections, MaxConnections);

    public SftpRemoteClient(RemoteSource source, string password)
    {
        _source = source;
        _password = password;
        _hostKey = new ServerPin(source.HostKeyFingerprint, isCertificate: false);
        _trustedKeyType = source.HostKeyAlgorithm;
    }

    public event Action<ServerIdentity>? IdentityLearned
    {
        add => _hostKey.Learned += value;
        remove => _hostKey.Learned -= value;
    }

    public event Action<ServerIdentity>? IdentityRejected
    {
        add => _hostKey.Rejected += value;
        remove => _hostKey.Rejected -= value;
    }

    private SftpClient Rent()
    {
        _slots.Wait();
        ServerIdentity? refused = null;
        try
        {
            if (_idle.TryTake(out var client) && client.IsConnected) return client;
            client?.Dispose();
            client = new SftpClient(_source.Address.Trim(), _source.EffectivePort, _source.UserName, _password);
            client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(15);
            // Unchecked, SSH.NET trusts any host key - whoever answers in between would be sent the
            // password. Only the key trusted on the first connection is accepted (before signing in).
            client.HostKeyReceived += (_, e) =>
            {
                var presented = ServerIdentity.ForHostKey(e.FingerPrintSHA256, e.HostKeyName);
                e.CanTrust = _hostKey.Check(presented);
                if (!e.CanTrust) refused = presented;
            };
            // A server has a key of each kind (ed25519, RSA...) and shows the first kind the client
            // asks for; SSH.NET's own order may change with an update, and the other key would look
            // like a changed one. So the trusted kind is asked for first.
            if (_trustedKeyType is { Length: > 0 } keyType && client.ConnectionInfo.HostKeyAlgorithms.ContainsKey(keyType))
                client.ConnectionInfo.HostKeyAlgorithms.SetPosition(keyType, 0);
            client.Connect();
            _hostKey.Confirm();
            return client;
        }
        catch (Exception ex) when (refused is not null)
        {
            _slots.Release();
            throw new ServerIdentityChangedException(refused, ex);
        }
        catch (Renci.SshNet.Common.SshAuthenticationException)
        {
            _slots.Release();
            throw new IOException("사용자 이름 또는 비밀번호가 맞지 않습니다.");
        }
        catch
        {
            _slots.Release();
            throw;
        }
    }

    private void Return(SftpClient client)
    {
        if (client.IsConnected) _idle.Add(client);
        else client.Dispose();
        _slots.Release();
    }

    private T Use<T>(Func<SftpClient, T> action)
    {
        var client = Rent();
        try
        {
            return action(client);
        }
        finally
        {
            Return(client);
        }
    }

    public Task TestAsync(CancellationToken ct) => ListAsync("/", ct);

    public Task<IReadOnlyList<RemoteEntry>> ListAsync(string folder, CancellationToken ct) => Task.Run(() => Use(client =>
        (IReadOnlyList<RemoteEntry>)client.ListDirectory(string.IsNullOrEmpty(folder) ? "/" : folder)
            .Where(f => f.Name is not ("." or "..") && (f.IsDirectory || f.IsRegularFile))
            .Select(f => new RemoteEntry(f.Name, f.FullName, f.IsDirectory, f.Length, f.LastWriteTime))
            .ToList()), ct);

    public long GetSize(string path) => Use(client => client.GetAttributes(path).Size);

    public Stream OpenRead(string path, long offset)
    {
        var client = Rent();
        try
        {
            var stream = client.OpenRead(path);
            if (offset > 0) stream.Seek(offset, SeekOrigin.Begin);
            return new ReturningStream(stream, () => Return(client));
        }
        catch
        {
            Return(client);
            throw;
        }
    }

    public void Upload(string path, Stream content) => Use(client =>
    {
        var temp = path + ".tunesync-upload";
        client.UploadFile(content, temp, canOverride: true);
        try
        {
            client.RenameFile(temp, path, isPosix: true); // atomic replace where the server supports it
        }
        catch
        {
            if (client.Exists(path)) client.DeleteFile(path);
            client.RenameFile(temp, path);
        }
        return true;
    });

    public void Dispose()
    {
        while (_idle.TryTake(out var client)) client.Dispose();
    }
}
