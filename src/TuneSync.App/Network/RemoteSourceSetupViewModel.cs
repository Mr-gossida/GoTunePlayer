using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneSync.Core.Models;
using TuneSync.Core.Services;
using TuneSync.Core.Services.Remote;

namespace TuneSync.App.Network;

/// <summary>"네트워크 위치 추가": step 1 picks the kind and connection details (tested on the spot),
/// step 2 browses the server to pick the music folder.</summary>
public sealed partial class RemoteSourceSetupViewModel : ObservableObject, IDisposable
{
    private IRemoteClient? _browser;
    private CancellationTokenSource? _busy;

    /// <param name="saveOnly">설정 › 위치 추가: check the connection and save the location, without
    /// picking a folder (that's "폴더 추가" on the saved location, or the library's + menu).</param>
    public RemoteSourceSetupViewModel(IReadOnlyList<RemoteSource>? saved = null, bool saveOnly = false)
    {
        SaveOnly = saveOnly;
        SavedSources = (saved ?? []).Select(s => new SavedSourceOption(s, s.Name.Length > 0 ? s.Name : s.Address,
            $"{s.KindLabel} · {s.Address}" + (s.UserName.Length > 0 ? $" · {s.UserName}" : ""), IconFor(s.Kind))).ToList();
        Kinds =
        [
            new(RemoteKind.WebDav, "WebDAV", "NAS · Nextcloud · 클라우드", "Globe24"),
            new(RemoteKind.Smb, "Samba", "Windows 공유 폴더", "Server24"),
            new(RemoteKind.Sftp, "SFTP", "SSH 서버", "LockClosed24"),
            new(RemoteKind.Ftp, "FTP", "일반 FTP 서버", "ArrowSwap24"),
            new(RemoteKind.Ftps, "FTPS", "암호화된 FTP", "ShieldLock24"),
        ];
        _selectedKind = Kinds[0];
    }

    public IReadOnlyList<KindOption> Kinds { get; }

    // --- locations set up before: pick one to browse it again, no typing ---

    public IReadOnlyList<SavedSourceOption> SavedSources { get; }
    public bool HasSavedSources => !SaveOnly && SavedSources.Count > 0;

    /// <summary>Adding a location only (no folder step): see the constructor.</summary>
    public bool SaveOnly { get; }

    public string Heading => SaveOnly ? "어떤 위치를 추가할까요?" : "어디에 연결할까요?";

    public string Lead => SaveOnly
        ? "연결이 되는지 확인한 뒤 위치만 저장합니다. 음악 폴더는 저장한 위치의 '폴더 추가'에서 따로 고릅니다."
        : "NAS, 서버, 클라우드에 있는 음악을 바로 재생합니다. 처음 재생할 때 내려받아 두므로 다시 들을 때는 바로 나옵니다.";

    public string ConnectText => SaveOnly ? "확인 후 저장" : "연결";

    /// <summary>Set when a saved location is reused, so it is updated rather than added twice -
    /// as long as it is still that location (see <see cref="SourceId"/>).</summary>
    private RemoteSource? _existing;

    /// <summary>Raised when a saved password was filled in (the window shows it in its PasswordBox).</summary>
    public event EventHandler? PasswordLoaded;

    public static string IconFor(RemoteKind kind) => kind switch
    {
        RemoteKind.WebDav => "Globe24",
        RemoteKind.Smb => "Server24",
        RemoteKind.Sftp => "LockClosed24",
        RemoteKind.Ftps => "ShieldLock24",
        _ => "ArrowSwap24",
    };

    /// <summary>A saved location: its details and password are filled in and it connects right
    /// away, straight to its folders.</summary>
    [RelayCommand]
    private async Task UseSavedAsync(SavedSourceOption? option)
    {
        if (option is null || IsBusy) return;
        var s = option.Source;
        SelectedKind = Kinds.FirstOrDefault(k => k.Kind == s.Kind) ?? Kinds[0];
        Address = s.Address;
        Port = s.Port > 0 ? s.Port : null;
        UserName = s.UserName;
        Name = s.Name;
        AcceptAnyCertificate = s.AcceptAnyCertificate;
        try { Password = CredentialStore.Load(s.Id); } catch { Password = ""; }
        _existing = s;
        PasswordLoaded?.Invoke(this, EventArgs.Empty);
        await ConnectAsync();
    }

    // --- step 1: connection ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddressPlaceholder), nameof(AddressHint), nameof(ShowPort), nameof(PortPlaceholder),
        nameof(ShowCertificateOption), nameof(UserPlaceholder), nameof(SuggestedAddress), nameof(HasSuggestedAddress), nameof(SuggestedAddressText),
        nameof(ShowAddressExamples))]
    private KindOption _selectedKind;

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddressHint), nameof(SuggestedAddress), nameof(HasSuggestedAddress), nameof(SuggestedAddressText),
        nameof(ShowAddressExamples))]
    private string _address = "";

    [ObservableProperty]
    private double? _port;

    [ObservableProperty]
    private string _userName = "";

    /// <summary>Set from the PasswordBox (which can't be bound).</summary>
    public string Password { get; set; } = "";

    [ObservableProperty]
    private bool _acceptAnyCertificate;

    public RemoteKind Kind => SelectedKind.Kind;

    public string AddressPlaceholder => Kind switch
    {
        RemoteKind.WebDav => "https://nas.example.com/music",
        RemoteKind.Smb => @"\\NAS\Music",
        RemoteKind.Sftp => "nas.local 또는 192.168.0.10",
        _ => "ftp.example.com",
    };

    public string UserPlaceholder => Kind == RemoteKind.Smb ? "필요 없으면 비워 두세요" : "사용자 이름";

    public bool ShowPort => Kind is RemoteKind.Ftp or RemoteKind.Ftps or RemoteKind.Sftp;
    public string PortPlaceholder => Kind == RemoteKind.Sftp ? "22" : "21";
    public bool ShowCertificateOption => Kind is RemoteKind.WebDav or RemoteKind.Ftps;

    /// <summary>A one-line note under the address, changing with the kind (and warning about
    /// plain http, where the password would travel unencrypted).</summary>
    public string AddressHint => Kind switch
    {
        RemoteKind.WebDav when PortTypedAsFolder() is { } port
            => $"⚠ 주소 뒤의 /{port}은(는) 폴더 이름으로 읽힙니다. 포트라면 :{port}처럼 콜론으로 붙입니다.",
        RemoteKind.WebDav when Address.Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            => "⚠ http 주소는 비밀번호가 암호화되지 않고 전송됩니다. 가능하면 https를 사용하세요.",
        RemoteKind.WebDav => "WebDAV 서버 주소를 https://로 시작해 입력합니다. 기본 포트가 아니면 주소 뒤에 :포트번호를 붙입니다.",
        RemoteKind.Smb => "탐색기 주소창에 입력하는 공유 폴더 주소입니다. 이 PC에 이미 연결되어 있다면 로그인 정보는 비워 두어도 됩니다.",
        RemoteKind.Sftp => "SSH로 접속하는 서버 주소입니다. 대부분의 NAS와 리눅스 서버가 지원합니다.",
        RemoteKind.Ftps => "TLS로 암호화된 FTP(명시적 FTPS)입니다.",
        _ => "⚠ FTP는 비밀번호가 암호화되지 않습니다. 집 안 네트워크에서만 사용하세요.",
    };

    /// <summary>WebDAV: where to find the address on common servers. The port differs by product
    /// (5006 is only Synology's WebDAV Server default), so the examples name the product.</summary>
    public bool ShowAddressExamples => Kind == RemoteKind.WebDav && SuggestedAddress is null;

    /// <summary>"https://nas.com/5006" - a port typed after a slash, which makes it a folder.
    /// The address it was probably meant to be ("https://nas.com:5006"), or null.</summary>
    public string? SuggestedAddress =>
        PortAsFolderMatch() is { } match ? $"{match.Groups["host"].Value}:{match.Groups["port"].Value}{match.Groups["rest"].Value}" : null;

    public bool HasSuggestedAddress => SuggestedAddress is not null;

    /// <summary>The button that puts it right shows the address it will become.</summary>
    public string SuggestedAddressText => SuggestedAddress is { } fixedAddress ? $"{fixedAddress}(으)로 고치기" : "";

    private int? PortTypedAsFolder() => PortAsFolderMatch() is { } match ? int.Parse(match.Groups["port"].Value) : null;

    private System.Text.RegularExpressions.Match? PortAsFolderMatch()
    {
        if (Kind != RemoteKind.WebDav) return null;
        var match = PortAsFolder().Match(Address.Trim());
        return match.Success && int.TryParse(match.Groups["port"].Value, out var port) && port is >= 1 and <= 65535 ? match : null;
    }

    [RelayCommand]
    private void UseSuggestedAddress()
    {
        if (SuggestedAddress is { } fixedAddress) Address = fixedAddress;
    }

    // scheme://host (no port yet) / 2-5 digits / the rest
    [System.Text.RegularExpressions.GeneratedRegex(@"^(?<host>https?://[^/:?#]+)/(?<port>\d{2,5})(?<rest>(/.*)?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex PortAsFolder();

    // --- shared state ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnectStep), nameof(IsBrowseStep))]
    private int _step;

    public bool IsConnectStep => Step == 0;
    public bool IsBrowseStep => Step == 1;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorText = "";

    public bool HasError => ErrorText.Length > 0;

    // --- step 2: browse ---

    public ObservableCollection<BrowseItem> Folders { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoUp), nameof(CurrentFolderLabel))]
    private string _currentFolder = "/";

    [ObservableProperty]
    private string _musicCountText = "";

    [ObservableProperty]
    private bool _includeSubfolders = true;

    public string RootFolder => Kind == RemoteKind.Smb ? SmbRoot : "/";
    public bool CanGoUp => !string.Equals(CurrentFolder.TrimEnd('/', '\\'), RootFolder.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);
    public string CurrentFolderLabel => Kind == RemoteKind.Smb ? CurrentFolder : $"{DisplayName}  {CurrentFolder}";

    private string SmbRoot => @"\\" + Address.Trim().TrimStart('\\').TrimEnd('\\');

    public string DisplayName => Name.Trim().Length > 0 ? Name.Trim() : DefaultName();

    private string DefaultName()
    {
        var a = Address.Trim();
        if (Uri.TryCreate(a.Contains("://") ? a : "x://" + a.TrimStart('\\').Replace('\\', '/'), UriKind.Absolute, out var u))
            return u.Host;
        return a;
    }

    /// <summary>Filled in when the user picks a folder; the window closes then.</summary>
    public (RemoteSource Source, string Password, LibraryFolderEntry? Folder)? Result { get; private set; }

    public event EventHandler? Finished;

    /// <summary>The saved location's id while the kind, address and user name are still its own;
    /// a different location gets a new id. (Going back from a saved location and typing another
    /// address used to keep the old id, so the new location replaced it instead of being added.)
    /// The same location set up again updates the saved one rather than doubling it.</summary>
    private string SourceId()
    {
        bool Same(RemoteSource s) => s.Kind == Kind
            && string.Equals(s.Address.Trim().TrimEnd('/', '\\'), Address.Trim().TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase)
            && string.Equals(s.UserName.Trim(), UserName.Trim(), StringComparison.OrdinalIgnoreCase);
        if (_existing is not null && Same(_existing)) return _existing.Id;
        return SavedSources.Select(o => o.Source).FirstOrDefault(Same)?.Id ?? Guid.NewGuid().ToString("N");
    }

    public RemoteSource BuildSource() => new()
    {
        Id = SourceId(),
        Name = DisplayName,
        Kind = Kind,
        Address = Address.Trim(),
        Port = Port is > 0 and < 65536 ? (int)Port.Value : 0,
        UserName = UserName.Trim(),
        AcceptAnyCertificate = AcceptAnyCertificate,
    };

    [RelayCommand]
    private async Task ConnectAsync()
    {
        ErrorText = "";
        if (Address.Trim().Length == 0)
        {
            ErrorText = "주소를 입력해 주세요.";
            return;
        }

        var source = BuildSource();
        await RunBusyAsync("연결하는 중…", async ct =>
        {
            await NetworkLibrary.TestAsync(source, Password, ct);
            if (SaveOnly)
            {
                // it answers: keep the location, pick folders later
                Result = (source, Password, null);
                Finished?.Invoke(this, EventArgs.Empty);
                return;
            }
            _browser?.Dispose();
            _browser = Kind == RemoteKind.Smb ? null : NetworkLibrary.CreateClient(source, Password);
            Step = 1;
            await LoadFolderAsync(RootFolder, ct);
        });
    }

    [RelayCommand]
    private void Back()
    {
        ErrorText = "";
        _busy?.Cancel();
        Step = 0;
    }

    [RelayCommand]
    private async Task OpenAsync(BrowseItem? item)
    {
        if (item is null || IsBusy) return;
        await RunBusyAsync("불러오는 중…", ct => LoadFolderAsync(item.Path, ct));
    }

    [RelayCommand]
    private async Task UpAsync()
    {
        if (!CanGoUp || IsBusy) return;
        var parent = Kind == RemoteKind.Smb
            ? Path.GetDirectoryName(CurrentFolder.TrimEnd('\\')) ?? RootFolder
            : CurrentFolder.TrimEnd('/')[..Math.Max(1, CurrentFolder.TrimEnd('/').LastIndexOf('/'))];
        if (parent.Length < RootFolder.Length) parent = RootFolder;
        await RunBusyAsync("불러오는 중…", ct => LoadFolderAsync(parent, ct));
    }

    [RelayCommand]
    private void AddFolder()
    {
        var source = BuildSource();
        Result = (source, Password, new LibraryFolderEntry
        {
            Path = CurrentFolder,
            IncludeSubfolders = IncludeSubfolders,
            RemoteSourceId = source.Id,
        });
        Finished?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoadFolderAsync(string folder, CancellationToken ct)
    {
        IReadOnlyList<RemoteEntry> entries;
        if (Kind == RemoteKind.Smb)
        {
            entries = await Task.Run(() =>
            {
                var dir = new DirectoryInfo(folder);
                return (IReadOnlyList<RemoteEntry>)dir.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true })
                    .Select(i => new RemoteEntry(i.Name, i.FullName, i is DirectoryInfo, i is FileInfo f ? f.Length : 0, i.LastWriteTime))
                    .ToList();
            }, ct);
        }
        else
        {
            entries = await _browser!.ListAsync(folder, ct);
        }

        Folders.Clear();
        foreach (var entry in entries.Where(e => e.IsDirectory && !e.Name.StartsWith('.') && !e.Name.StartsWith('@') && !e.Name.StartsWith('#'))
                                     .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
            Folders.Add(new BrowseItem(entry.Name, entry.Path));
        var songs = entries.Count(e => !e.IsDirectory && TagService.IsSupported(e.Name));
        CurrentFolder = folder;
        MusicCountText = songs > 0 ? $"이 폴더에 음악 {songs}곡" : Folders.Count > 0 ? "이 폴더에는 음악 파일이 없습니다 (하위 폴더 포함 가능)" : "빈 폴더입니다";
    }

    private async Task RunBusyAsync(string text, Func<CancellationToken, Task> work)
    {
        _busy?.Cancel();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        _busy = cts;
        IsBusy = true;
        BusyText = text;
        ErrorText = "";
        try
        {
            await work(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && _busy != cts)
        {
            // superseded
        }
        catch (OperationCanceledException)
        {
            ErrorText = "응답이 없습니다. 주소와 포트, 네트워크 연결을 확인해 주세요.";
        }
        catch (Exception ex)
        {
            ErrorText = Friendly(ex);
        }
        finally
        {
            if (_busy == cts) IsBusy = false;
        }
    }

    private static string Friendly(Exception ex) => ex switch
    {
        System.Net.Http.HttpRequestException { InnerException: System.Security.Authentication.AuthenticationException }
            => "인증서를 신뢰할 수 없습니다. 자체 서명 인증서를 쓰는 NAS라면 '인증서 확인 안 함'을 켜 주세요.",
        System.Net.Http.HttpRequestException => "서버에 연결할 수 없습니다. 주소를 확인해 주세요.",
        System.Net.Sockets.SocketException => "서버에 연결할 수 없습니다. 주소와 포트를 확인해 주세요.",
        Renci.SshNet.Common.SshConnectionException => "SSH 연결이 끊겼습니다. 주소와 포트를 확인해 주세요.",
        FluentFTP.Exceptions.FtpAuthenticationException => "사용자 이름 또는 비밀번호가 맞지 않습니다.",
        IOException or UnauthorizedAccessException => ex.Message,
        _ => ex.InnerException?.Message ?? ex.Message,
    };

    public void Dispose()
    {
        _busy?.Cancel();
        _browser?.Dispose();
    }
}

public sealed record KindOption(RemoteKind Kind, string Title, string Subtitle, string Icon);

public sealed record BrowseItem(string Name, string Path);

public sealed record SavedSourceOption(RemoteSource Source, string Title, string Detail, string Icon);
