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

    public RemoteSourceSetupViewModel()
    {
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

    // --- step 1: connection ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddressPlaceholder), nameof(AddressHint), nameof(ShowPort), nameof(PortPlaceholder),
        nameof(ShowCertificateOption), nameof(UserPlaceholder))]
    private KindOption _selectedKind;

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddressHint))]
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
        RemoteKind.WebDav => "https://nas.local:5006/music",
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
        RemoteKind.WebDav when Address.Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            => "⚠ http 주소는 비밀번호가 암호화되지 않고 전송됩니다. 가능하면 https를 사용하세요.",
        RemoteKind.WebDav => "Synology는 WebDAV Server 패키지를 켠 뒤 https://주소:5006 형태로 입력합니다.",
        RemoteKind.Smb => "탐색기 주소창에 입력하는 공유 폴더 주소입니다. 이 PC에 이미 연결되어 있다면 로그인 정보는 비워 두어도 됩니다.",
        RemoteKind.Sftp => "SSH로 접속하는 서버 주소입니다. 대부분의 NAS와 리눅스 서버가 지원합니다.",
        RemoteKind.Ftps => "TLS로 암호화된 FTP(명시적 FTPS)입니다.",
        _ => "⚠ FTP는 비밀번호가 암호화되지 않습니다. 집 안 네트워크에서만 사용하세요.",
    };

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
    public (RemoteSource Source, string Password, LibraryFolderEntry Folder)? Result { get; private set; }

    public event EventHandler? Finished;

    public RemoteSource BuildSource() => new()
    {
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
