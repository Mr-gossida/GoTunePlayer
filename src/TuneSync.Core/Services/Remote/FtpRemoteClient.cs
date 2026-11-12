using System.Collections.Concurrent;
using FluentFTP;
using TuneSync.Core.Models;

namespace TuneSync.Core.Services.Remote;

/// <summary>FTP and FTPS (explicit TLS). An FTP connection can do one transfer at a time, so a
/// small pool of connections serves the scanner, the player and edits side by side.</summary>
public sealed class FtpRemoteClient : IRemoteClient
{
    private const int MaxConnections = 4;
    private readonly RemoteSource _source;
    private readonly string _password;
    private readonly ConcurrentBag<FtpClient> _idle = [];
    private readonly SemaphoreSlim _slots = new(MaxConnections, MaxConnections);

    public FtpRemoteClient(RemoteSource source, string password)
    {
        _source = source;
        _password = password;
    }

    private FtpClient Rent()
    {
        _slots.Wait();
        try
        {
            if (_idle.TryTake(out var client) && client.IsConnected) return client;
            client?.Dispose();
            client = new FtpClient(_source.Address.Trim(), _source.UserName, _password, _source.EffectivePort);
            client.Config.EncryptionMode = _source.Kind == RemoteKind.Ftps ? FtpEncryptionMode.Explicit : FtpEncryptionMode.None;
            client.Config.ValidateAnyCertificate = _source.AcceptAnyCertificate;
            client.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;
            client.Config.ConnectTimeout = 15000;
            client.Encoding = System.Text.Encoding.UTF8;
            client.Connect();
            return client;
        }
        catch
        {
            _slots.Release();
            throw;
        }
    }

    private void Return(FtpClient client)
    {
        if (client.IsConnected) _idle.Add(client);
        else client.Dispose();
        _slots.Release();
    }

    private T Use<T>(Func<FtpClient, T> action)
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
        (IReadOnlyList<RemoteEntry>)client.GetListing(string.IsNullOrEmpty(folder) ? "/" : folder)
            .Where(i => i.Type is FtpObjectType.File or FtpObjectType.Directory)
            .Select(i => new RemoteEntry(i.Name, i.FullName, i.Type == FtpObjectType.Directory, i.Size, i.Modified))
            .ToList()), ct);

    public long GetSize(string path) => Use(client => client.GetFileSize(path));

    public Stream OpenRead(string path, long offset)
    {
        var client = Rent();
        try
        {
            var stream = client.OpenRead(path, FtpDataType.Binary, offset);
            return new ReturningStream(stream, () =>
            {
                try { client.GetReply(); } catch { /* transfer cut short on purpose - reconnect next time */ client.Disconnect(); }
                Return(client);
            });
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
        var status = client.UploadStream(content, temp, FtpRemoteExists.Overwrite);
        if (status != FtpStatus.Success) throw new IOException("업로드하지 못했습니다.");
        if (!client.MoveFile(temp, path, FtpRemoteExists.Overwrite)) throw new IOException("업로드한 파일로 바꾸지 못했습니다.");
        return true;
    });

    public void Dispose()
    {
        while (_idle.TryTake(out var client)) client.Dispose();
    }
}
