using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using TuneSync.Core.Models;
using TuneSync.Core.Services;
using TuneSync.Core.Services.Remote;

namespace TuneSync.App.Network;

/// <summary>Everything about network locations in one place: the saved locations and their
/// passwords, a connected client per location, signing in to Samba shares, the streaming cache and
/// local stream server, and the index that makes a remote library open quickly. It also plugs the
/// cache into <see cref="RemoteFiles"/>, so tag edits on network songs are made on the cached copy
/// and uploaded back.</summary>
public sealed class NetworkLibrary : IDisposable
{
    private readonly AppSettings _settings;
    private readonly ConcurrentDictionary<string, IRemoteClient> _clients = new();

    public NetworkLibrary(AppSettings settings)
    {
        _settings = settings;
        Cache = new StreamingCache(() => Math.Max(256, _settings.StreamCacheMegabytes) * 1024L * 1024L);
        Server = new LocalStreamServer();
        Index = new LibraryIndex(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TuneSync", "remote-index.json"));

        RemoteFiles.ClientFor = ClientFor;
        ViewModels.LibraryFileViewModel.RemoteSourceName = id => Find(id)?.Name;
        RemoteFiles.LocalCopyOf = Cache.CompletedPath;
        RemoteFiles.Uploaded = (path, _) =>
        {
            Index.Forget(path);
            Cache.Open(path).Refresh();
        };
    }

    public StreamingCache Cache { get; }
    public LocalStreamServer Server { get; }
    public LibraryIndex Index { get; }

    public IReadOnlyList<RemoteSource> Sources => _settings.RemoteSources;

    public RemoteSource? Find(string? id) => id is null ? null : _settings.RemoteSources.FirstOrDefault(s => s.Id == id);

    public IRemoteClient? ClientFor(string sourceId)
    {
        if (_clients.TryGetValue(sourceId, out var existing)) return existing;
        if (Find(sourceId) is not { } source || source.Kind == RemoteKind.Smb) return null;
        var client = RemoteClientFactory.Create(source, CredentialStore.Load(sourceId));
        return _clients.GetOrAdd(sourceId, client);
    }

    /// <summary>Tries the connection with these details (nothing is saved).</summary>
    public static async Task TestAsync(RemoteSource source, string password, CancellationToken ct)
    {
        if (source.Kind == RemoteKind.Smb)
        {
            await Task.Run(() => ConnectSmb(source, password), ct);
            return;
        }
        using var client = RemoteClientFactory.Create(source, password);
        await client.TestAsync(ct);
    }

    /// <summary>A client for browsing while setting a location up (before it's saved).</summary>
    public static IRemoteClient CreateClient(RemoteSource source, string password) => RemoteClientFactory.Create(source, password);

    public void Save(RemoteSource source, string password)
    {
        CredentialStore.Save(source.Id, source.UserName, password);
        if (Find(source.Id) is null) _settings.RemoteSources.Add(source);
        if (_clients.TryRemove(source.Id, out var old)) old.Dispose();
        AppSettingsStore.Save(_settings);
    }

    /// <summary>Forgets a location and its password (library entries are removed by the caller).</summary>
    public void Remove(string sourceId)
    {
        _settings.RemoteSources.RemoveAll(s => s.Id == sourceId);
        CredentialStore.Delete(sourceId);
        if (_clients.TryRemove(sourceId, out var client)) client.Dispose();
        AppSettingsStore.Save(_settings);
    }

    /// <summary>Signs in to the Samba share a library folder is on, if it has saved details.
    /// Run off the UI thread (it waits on the network).</summary>
    public bool EnsureConnected(LibraryFolderEntry entry)
    {
        if (Find(entry.RemoteSourceId) is not { Kind: RemoteKind.Smb } source) return true;
        try
        {
            ConnectSmb(source, CredentialStore.Load(source.Id));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>"거실 NAS › /Music/KPop" for a remote folder; the plain path otherwise.</summary>
    public string Describe(LibraryFolderEntry entry) =>
        Find(entry.RemoteSourceId) is { } source && entry.IsRemote ? $"{source.Name} › {entry.Path}" : entry.Path;

    // --- Samba: Windows' own network connection, with the saved user name and password ---

    private static void ConnectSmb(RemoteSource source, string password)
    {
        var share = source.Address.Trim().TrimEnd('\\');
        if (!share.StartsWith(@"\\", StringComparison.Ordinal)) share = @"\\" + share.TrimStart('\\');
        var resource = new NETRESOURCE { dwType = RESOURCETYPE_DISK, lpRemoteName = share };
        var result = WNetAddConnection2(ref resource,
            string.IsNullOrEmpty(source.UserName) ? null : password,
            string.IsNullOrEmpty(source.UserName) ? null : source.UserName, 0);
        // 0 = connected; 1219 = already connected under these/other details (it still works).
        if (result is 0 or 1219) return;
        throw new IOException(result switch
        {
            5 or 86 or 1326 => "사용자 이름 또는 비밀번호가 맞지 않습니다.",
            53 or 67 => "공유 폴더를 찾을 수 없습니다. 주소(\\\\서버\\공유)를 확인해 주세요.",
            1203 => "네트워크 위치를 찾을 수 없습니다.",
            _ => new System.ComponentModel.Win32Exception(result).Message,
        });
    }

    private const int RESOURCETYPE_DISK = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NETRESOURCE
    {
        public int dwScope;
        public int dwType;
        public int dwDisplayType;
        public int dwUsage;
        public string? lpLocalName;
        public string? lpRemoteName;
        public string? lpComment;
        public string? lpProvider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NETRESOURCE resource, string? password, string? userName, int flags);

    public void Dispose()
    {
        Index.Save();
        Server.Dispose();
        foreach (var client in _clients.Values) client.Dispose();
    }
}
