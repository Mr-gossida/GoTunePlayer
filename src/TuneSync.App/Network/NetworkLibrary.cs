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

    /// <summary>"Location|new key" already warned about (see <see cref="WarnIdentityChanged"/>).</summary>
    private readonly ConcurrentDictionary<string, byte> _identityWarnings = new();

    public NetworkLibrary(AppSettings settings)
    {
        _settings = settings;
        Cache = new StreamingCache(() => Math.Max(256, _settings.StreamCacheMegabytes) * 1024L * 1024L);
        Server = new LocalStreamServer();
        Index = new LibraryIndex(TuneSync.Core.Services.AppDataFolder.Combine("remote-index.json"));

        RemoteFiles.ClientFor = ClientFor;
        ViewModels.LibraryFileViewModel.RemoteSourceName = id => Find(id)?.Name;
        RemoteFiles.LocalCopyOf = Cache.CompletedPath;
        RemoteFiles.Uploaded = (path, _) =>
        {
            Index.Forget(path);
            Cache.Open(path).Refresh();
        };
        RemoteFiles.Discarded = path =>
        {
            Index.Forget(path);
            Cache.Forget(path);
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
        if (client is IServerIdentityCheck check)
        {
            check.IdentityLearned += identity => OnUiThread(() => KeepLearned(sourceId, identity));
            check.IdentityRejected += identity => OnUiThread(() => WarnIdentityChanged(sourceId, identity));
        }
        return _clients.GetOrAdd(sourceId, client);
    }

    // --- who the servers are: SSH host keys and self-signed certificates, trusted on first use ---

    /// <summary>A location connected for the first time since server keys/certificates are checked:
    /// what its server showed is kept, so a different one is noticed from now on.</summary>
    private void KeepLearned(string sourceId, ServerIdentity identity)
    {
        if (Find(sourceId) is not { } source) return;
        var pinned = identity.IsCertificate ? source.CertificateThumbprint : source.HostKeyFingerprint;
        if (pinned is not null) return; // trusted meanwhile in the setup window
        Pin(source, identity);
        AppSettingsStore.Save(_settings);
    }

    /// <summary>The setup window's "새 키 신뢰" on a saved location: kept right away (even if no folder
    /// is added afterwards), and its client reconnects with it.</summary>
    public void Trust(string sourceId, ServerIdentity identity)
    {
        if (Find(sourceId) is not { } source) return;
        Pin(source, identity);
        if (_clients.TryRemove(sourceId, out var old)) old.Dispose();
        // a key warned about before and showing up again later is worth a warning again
        foreach (var warned in _identityWarnings.Keys.Where(k => k.StartsWith(sourceId + "|", StringComparison.Ordinal)))
            _identityWarnings.TryRemove(warned, out _);
        AppSettingsStore.Save(_settings);
    }

    private static void Pin(RemoteSource source, ServerIdentity identity)
    {
        if (identity.IsCertificate)
        {
            source.CertificateThumbprint = identity.Fingerprint;
        }
        else
        {
            source.HostKeyFingerprint = identity.Fingerprint;
            source.HostKeyAlgorithm = identity.Algorithm;
        }
    }

    /// <summary>A scan, a check or playback was refused because the server showed another key or
    /// certificate. Nothing asks in the background; this says why it stopped, what the server showed
    /// and where to trust it - once per location and new key, as scans and reconnect checks keep trying.</summary>
    private void WarnIdentityChanged(string sourceId, ServerIdentity presented)
    {
        if (!_identityWarnings.TryAdd($"{sourceId}|{presented.Fingerprint}", 0)) return;
        var name = Find(sourceId)?.Name ?? "네트워크 위치";
        if (presented.IsCertificate)
            Toast.Warning($"'{name}'의 인증서가 바뀌어 연결을 막았습니다",
                "인증서를 새로 발급했거나 NAS를 다시 설치했다면 설정 › 라이브러리 › 네트워크 위치에서 이 위치의 '폴더 추가'로 다시 연결해 새 인증서를 확인해 주세요. "
                + $"그런 적이 없다면 누군가 연결을 가로채고 있을 수 있습니다. (새 인증서 SHA-256: {presented.DisplayFingerprint})");
        else
            Toast.Warning($"'{name}'의 SSH 키가 바뀌어 연결을 막았습니다",
                "서버를 다시 설치했거나 SSH 키를 바꿨다면 설정 › 라이브러리 › 네트워크 위치에서 이 위치의 '폴더 추가'로 다시 연결해 새 키를 확인해 주세요. "
                + $"그런 적이 없다면 누군가 연결을 가로채고 있을 수 있습니다. (새 키: {presented.Algorithm} {presented.DisplayFingerprint})");
    }

    /// <summary>Settings are changed and saved on the UI thread, like every other settings change;
    /// clients report from their connection threads.</summary>
    private static void OnUiThread(Action action)
    {
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher) dispatcher.BeginInvoke(action);
        else action();
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

    /// <summary>A client for trying and browsing a location while setting it up (before it's saved).</summary>
    public static IRemoteClient CreateClient(RemoteSource source, string password) => RemoteClientFactory.Create(source, password);

    public void Save(RemoteSource source, string password)
    {
        CredentialStore.Save(source.Id, source.UserName, password);
        if (Find(source.Id) is { } existing)
        {
            existing.Name = source.Name;
            existing.Kind = source.Kind;
            existing.Address = source.Address;
            existing.Port = source.Port;
            existing.UserName = source.UserName;
            existing.AcceptAnyCertificate = source.AcceptAnyCertificate;
            existing.CertificateThumbprint = source.CertificateThumbprint;
            existing.HostKeyFingerprint = source.HostKeyFingerprint;
            existing.HostKeyAlgorithm = source.HostKeyAlgorithm;
        }
        else
        {
            _settings.RemoteSources.Add(source);
        }
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
