using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.App.ViewModels;

/// <summary>The library: every file from the registered folders/files, plus two independent
/// views of it - <see cref="FilesView"/> for the 태그 관리 window (with its 누락 filters and
/// column sorting) and <see cref="PlaylistView"/> for the player (its own sort and grouping,
/// which also decide playback order).</summary>
public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly AppSettings _settings;

    /// <summary>Every song. Filled in batches (<see cref="BulkObservableCollection{T}.AddRange"/>):
    /// listeners see a Reset per batch rather than an Add per song.</summary>
    public BulkObservableCollection<LibraryFileViewModel> Files { get; } = [];
    public ICollectionView FilesView { get; }
    public ListCollectionView PlaylistView { get; }

    public IReadOnlyList<Choice<PlaylistSortKey>> PlaylistSortOptions { get; } =
    [
        new(PlaylistSortKey.FileName, "파일명"),
        new(PlaylistSortKey.Path, "경로"),
        new(PlaylistSortKey.Artist, "아티스트"),
        new(PlaylistSortKey.Title, "제목"),
        new(PlaylistSortKey.Album, "앨범"),
        new(PlaylistSortKey.Duration, "길이"),
        new(PlaylistSortKey.Custom, "직접 정렬 (끌어서 옮긴 순서)"),
    ];

    public IReadOnlyList<Choice<PlaylistGroupKey>> PlaylistGroupOptions { get; } =
    [
        new(PlaylistGroupKey.None, "그룹 없음"),
        new(PlaylistGroupKey.Folder, "폴더별"),
        new(PlaylistGroupKey.Artist, "아티스트별"),
        new(PlaylistGroupKey.Album, "앨범별"),
    ];

    [ObservableProperty]
    private PlaylistSortKey _playlistSort;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortDirectionToolTip))]
    private bool _playlistSortDescending;

    [ObservableProperty]
    private PlaylistGroupKey _playlistGroup;

    public string SortDirectionToolTip => PlaylistSortDescending ? "내림차순" : "오름차순";
    public FilterPanelViewModel Filter { get; } = new();
    public ObservableCollection<LibraryFolderViewModel> Folders { get; } = [];

    [ObservableProperty]
    private LibraryViewMode _viewMode = LibraryViewMode.List;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _statusText = "폴더를 추가해 주세요.";

    private CancellationTokenSource? _scanCts;

    private readonly Network.NetworkLibrary _network;

    public LibraryViewModel(AppSettings settings, Network.NetworkLibrary network)
    {
        _settings = settings;
        _network = network;
        FilesView = CollectionViewSource.GetDefaultView(Files);
        FilesView.Filter = o => o is LibraryFileViewModel vm && Filter.Matches(vm);
        // Files stream in from parallel readers in no particular order; keep the list (and so
        // the playlist and next/previous order) stable and predictable.
        FilesView.SortDescriptions.Add(new SortDescription(nameof(LibraryFileViewModel.FileName), ListSortDirection.Ascending));
        Filter.FilterChanged += (_, _) => FilesView.Refresh();

        PlaylistView = new ListCollectionView(Files);
        ((System.Collections.Specialized.INotifyCollectionChanged)PlaylistView).CollectionChanged += (_, _) => ScheduleGroupHeaders();
        _playlistSort = settings.PlaylistSort;
        _playlistSortDescending = settings.PlaylistSortDescending;
        _playlistGroup = settings.PlaylistGroup;
        ApplyPlaylistOrder();

        Files.CollectionChanged += (_, _) => NotifyLibraryState();
        Folders.CollectionChanged += (_, _) => NotifyLibraryState();

        foreach (var entry in settings.LibraryFolders)
            Folders.Add(new LibraryFolderViewModel(entry));
        _excluded = new HashSet<string>(settings.ExcludedSongs, StringComparer.OrdinalIgnoreCase);

        if (Folders.Count > 0) _ = RescanAsync();
    }

    public bool HasNoFiles => Files.Count == 0;

    // ---- taking songs out of the play list (their files stay where they are) ----

    private readonly HashSet<string> _excluded;

    /// <summary>How many songs were taken out (and can be put back).</summary>
    public int ExcludedCount => _excluded.Count;

    /// <summary>The rows selected in the play list (set by the view), for the − menu.</summary>
    public IReadOnlyList<LibraryFileViewModel> QueueSelection { get; set; } = [];

    /// <summary>Raised before songs leave the list, so playback can let go of them.</summary>
    public event Action<IReadOnlyList<LibraryFileViewModel>>? FilesRemoving;

    private static string SourcePath(LibraryFolderViewModel source) =>
        source.Entry.IsRemote ? RemotePath.Build(source.Entry.RemoteSourceId!, source.Path) : source.Path;

    /// <summary>재생 목록에서 빼기: the songs leave the list and stay out on later scans. A song
    /// that was added on its own (a file, not a folder) is simply unregistered. Nothing on disk
    /// changes; '뺀 곡 다시 넣기' brings them all back.</summary>
    public async Task RemoveFromListAsync(IReadOnlyList<LibraryFileViewModel> files)
    {
        if (files.Count == 0) return;
        var unsaved = files.Count(f => f.HasPendingChanges);
        if (unsaved > 0 && !await Dialogs.ConfirmAsync("목록에서 빼기",
                $"저장하지 않은 태그 변경이 있는 곡 {unsaved}곡이 포함되어 있습니다. 목록에서 빼면 그 변경은 사라집니다.\n\n계속할까요?", "빼기"))
            return;

        FilesRemoving?.Invoke(files);
        var paths = files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ownSources = Folders.Where(f => f.IsFile && paths.Contains(SourcePath(f))).ToList();
        foreach (var source in ownSources) Folders.Remove(source);
        var unregistered = ownSources.Select(SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Where(p => !unregistered.Contains(p))) _excluded.Add(path);
        if (ownSources.Count > 0) SaveFolders();
        SaveExcluded();

        Files.RemoveWhere(f => paths.Contains(f.Path));
        StatusText = $"{Files.Count}개 파일";
        Toast.Info(files.Count == 1 ? "목록에서 뺐습니다" : $"{files.Count}곡을 목록에서 뺐습니다",
            "파일은 그대로 있습니다. 목록 아래 − 메뉴의 '뺀 곡 다시 넣기'로 되돌릴 수 있습니다.");
    }

    /// <summary>'뺀 곡 다시 넣기': every song taken out comes back.</summary>
    [RelayCommand]
    private async Task RestoreRemovedAsync()
    {
        if (_excluded.Count == 0) return;
        var count = _excluded.Count;
        _excluded.Clear();
        SaveExcluded();
        Toast.Info("뺀 곡을 다시 넣었습니다", $"{count}곡을 목록으로 되돌립니다.");
        await RescanAsync();
    }

    /// <summary>Picking a folder or file again means wanting its songs: those taken out before come back.</summary>
    private bool Unexclude(IEnumerable<LibraryFolderEntry> entries)
    {
        if (_excluded.Count == 0) return false;
        var removed = 0;
        foreach (var entry in entries)
        {
            var root = entry.IsRemote ? RemotePath.Build(entry.RemoteSourceId!, entry.Path) : entry.Path;
            var prefix = root.TrimEnd('\\', '/');
            removed += _excluded.RemoveWhere(p => string.Equals(p, root, StringComparison.OrdinalIgnoreCase)
                                                || p.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase)
                                                || p.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));
        }
        if (removed > 0) SaveExcluded();
        return removed > 0;
    }

    private void SaveExcluded()
    {
        _settings.ExcludedSongs = [.. _excluded];
        AppSettingsStore.Save(_settings);
        OnPropertyChanged(nameof(ExcludedCount));
    }

    /// <summary>Nothing registered yet: the welcome screen.</summary>
    public bool IsWelcome => Folders.Count == 0 && Files.Count == 0;

    /// <summary>Folders registered, their songs still on the way (the first load).</summary>
    public bool IsLoadingEmpty => Folders.Count > 0 && Files.Count == 0 && IsScanning;

    /// <summary>Folders registered but none could be reached - typically a NAS or server that is
    /// switched off. Watched in the background; the list comes back by itself once it's reachable.</summary>
    public bool IsOffline => Folders.Count > 0 && Files.Count == 0 && !IsScanning && UnreachableCount > 0;

    /// <summary>Folders registered and reachable, but without any songs in them.</summary>
    public bool IsNoSongs => Folders.Count > 0 && Files.Count == 0 && !IsScanning && UnreachableCount == 0;

    /// <summary>Registered folders/files the last scan couldn't reach.</summary>
    [ObservableProperty]
    private int _unreachableCount;

    [ObservableProperty]
    private string _unreachableNames = "";

    private void NotifyLibraryState()
    {
        OnPropertyChanged(nameof(HasNoFiles));
        OnPropertyChanged(nameof(IsWelcome));
        OnPropertyChanged(nameof(IsLoadingEmpty));
        OnPropertyChanged(nameof(IsOffline));
        OnPropertyChanged(nameof(IsNoSongs));
    }

    partial void OnIsScanningChanged(bool value) => NotifyLibraryState();
    partial void OnUnreachableCountChanged(int value) => NotifyLibraryState();

    // ---- coming back after a server / NAS / drive was unreachable ----

    private List<LibraryFolderViewModel> _unreachable = [];
    private System.Windows.Threading.DispatcherTimer? _reconnectTimer;
    private bool _probing;

    /// <summary>"다시 연결": reads the registered folders again.</summary>
    [RelayCommand]
    private Task ReconnectAsync() => RescanAsync();

    private string DisplayName(LibraryFolderViewModel f)
    {
        var leaf = Path.GetFileName(f.Path.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(leaf)) leaf = f.Path;
        return f.Entry.RemoteSourceId is { } id && _network.Find(id) is { } source ? $"{source.Name} › {leaf}" : leaf;
    }

    /// <summary>After a scan: remembers what couldn't be reached and, while anything is missing,
    /// checks every 20 seconds (and whenever the network changes) whether it's back - then
    /// reloads the list on its own.</summary>
    private void WatchForReconnect(List<LibraryFolderViewModel> unreachable)
    {
        _unreachable = unreachable;
        var names = unreachable.Select(DisplayName).Distinct().ToList();
        UnreachableNames = string.Join(", ", names.Take(3)) + (names.Count > 3 ? $" 외 {names.Count - 3}개" : "");
        UnreachableCount = unreachable.Count;
        if (unreachable.Count == 0)
        {
            _reconnectTimer?.Stop();
            return;
        }
        if (_reconnectTimer is null)
        {
            _reconnectTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            _reconnectTimer.Tick += async (_, _) => await ProbeAsync();
            void Changed() => System.Windows.Application.Current?.Dispatcher.BeginInvoke(async () => await ProbeAsync());
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (_, e) => { if (e.IsAvailable) Changed(); };
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) => Changed();
        }
        _reconnectTimer.Start();
    }

    private async Task ProbeAsync()
    {
        if (_probing || IsScanning || _unreachable.Count == 0) return;
        _probing = true;
        try
        {
            var items = _unreachable.ToList();
            var back = false;
            foreach (var item in items)
                if (await IsReachableAsync(item)) { back = true; break; }
            if (back && !IsScanning)
            {
                _reconnectTimer?.Stop();
                Toast.Info("다시 연결되었습니다", "음악 목록을 다시 불러옵니다.");
                await RescanAsync();
            }
        }
        finally
        {
            _probing = false;
        }
    }

    /// <summary>Whether a registered folder/file answers now - with a time limit, since a
    /// switched-off server can keep a check waiting for a long time.</summary>
    private async Task<bool> IsReachableAsync(LibraryFolderViewModel f)
    {
        try
        {
            if (f.Entry.IsRemote)
            {
                if (_network.ClientFor(f.Entry.RemoteSourceId!) is not { } client) return false;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await client.TestAsync(cts.Token);
                return true;
            }
            var check = Task.Run(() =>
            {
                if (f.Entry.RemoteSourceId is not null) _network.EnsureConnected(f.Entry);
                return f.IsFile ? File.Exists(f.Path) : Directory.Exists(f.Path);
            });
            return await Task.WhenAny(check, Task.Delay(TimeSpan.FromSeconds(10))) == check && check.Result;
        }
        catch
        {
            return false;
        }
    }

    partial void OnPlaylistSortChanged(PlaylistSortKey value) => SavePlaylistOrder();
    partial void OnPlaylistSortDescendingChanged(bool value) => SavePlaylistOrder();
    partial void OnPlaylistGroupChanged(PlaylistGroupKey value) => SavePlaylistOrder();

    private bool _holdOrder;
    private Dictionary<string, int> _customRank = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Drag and drop in the play list: the songs go right before <paramref name="before"/>
    /// (or to the end). The list switches to 직접 정렬 - no groups, the dragged order saved - and
    /// says so the first time.</summary>
    public void MoveInQueue(IReadOnlyList<LibraryFileViewModel> moving, LibraryFileViewModel? before)
    {
        if (moving.Count == 0) return;
        var order = PlaylistView.Cast<LibraryFileViewModel>().ToList();
        var leaving = moving.ToHashSet();
        var rest = order.Where(f => !leaving.Contains(f)).ToList();
        var at = before is null ? rest.Count : rest.IndexOf(before);
        rest.InsertRange(at < 0 ? rest.Count : at, moving);
        _settings.CustomOrder = [.. rest.Select(f => f.Path)];

        var switched = PlaylistSort != PlaylistSortKey.Custom || PlaylistGroup != PlaylistGroupKey.None || PlaylistSortDescending;
        _holdOrder = true;
        try
        {
            PlaylistSort = PlaylistSortKey.Custom;
            PlaylistGroup = PlaylistGroupKey.None;
            PlaylistSortDescending = false;
        }
        finally
        {
            _holdOrder = false;
        }
        SavePlaylistOrder();
        if (switched)
            Toast.Info("직접 정렬로 바꿨습니다", "끌어서 옮긴 순서대로 재생하고, 이 순서는 저장됩니다. 정렬 메뉴에서 언제든 다른 정렬로 돌아갈 수 있습니다.");
    }

    [RelayCommand]
    private void TogglePlaylistSortDirection() => PlaylistSortDescending = !PlaylistSortDescending;

    private void SavePlaylistOrder()
    {
        if (_holdOrder) return;
        _settings.PlaylistSort = PlaylistSort;
        _settings.PlaylistSortDescending = PlaylistSortDescending;
        _settings.PlaylistGroup = PlaylistGroup;
        AppSettingsStore.Save(_settings);
        ApplyPlaylistOrder();
    }

    /// <summary>Groups are ordered by their name first, so the order you see (and next/previous
    /// follows) is: group by group, then the chosen sort inside each group.</summary>
    private void ApplyPlaylistOrder()
    {
        Func<LibraryFileViewModel, string>? group = PlaylistGroup switch
        {
            PlaylistGroupKey.Folder => f => f.Folder,
            PlaylistGroupKey.Artist => f => f.ArtistGroup,
            PlaylistGroupKey.Album => f => f.AlbumGroup,
            _ => null,
        };
        var text = StringComparer.CurrentCultureIgnoreCase;
        Comparison<LibraryFileViewModel> sort = PlaylistSort switch
        {
            PlaylistSortKey.Path => (a, b) => text.Compare(a.Path, b.Path),
            PlaylistSortKey.Artist => (a, b) => text.Compare(a.Artist, b.Artist),
            PlaylistSortKey.Title => (a, b) => text.Compare(a.DisplayTitle, b.DisplayTitle),
            PlaylistSortKey.Album => (a, b) => text.Compare(a.Album, b.Album),
            PlaylistSortKey.Duration => (a, b) => a.Duration.CompareTo(b.Duration),
            PlaylistSortKey.Custom => (a, b) => Rank(a).CompareTo(Rank(b)),
            _ => (a, b) => text.Compare(a.FileName, b.FileName),
        };
        if (PlaylistSort == PlaylistSortKey.Custom)
        {
            // Songs not arranged yet (added later) come after the arranged ones, by file name.
            _customRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < _settings.CustomOrder.Count; i++) _customRank.TryAdd(_settings.CustomOrder[i], i);
        }

        // Groups aren't WPF groups: grouping switched off the list's virtualization (a row was
        // built for every song on every refresh - seconds for a big library). The sort keeps each
        // group together, and UpdateGroupHeaders marks where each one starts.
        _groupKey = group;
        using (PlaylistView.DeferRefresh())
        {
            PlaylistView.GroupDescriptions.Clear();
            PlaylistView.SortDescriptions.Clear();
            // A comparer rather than SortDescriptions: those read every property by reflection on
            // every comparison, which made re-sorting a 1,200-song library take seconds.
            PlaylistView.CustomSort = new PlaylistOrder(group, sort, PlaylistSortDescending);
        }
        ScheduleGroupHeaders();
    }

    private int Rank(LibraryFileViewModel file) => _customRank.TryGetValue(file.Path, out var rank) ? rank : int.MaxValue;

    private Func<LibraryFileViewModel, string>? _groupKey;
    private bool _groupHeadersPending;

    /// <summary>Re-marks group starts once the queue has changed (coalesced: a batch of changes
    /// costs one pass), before the next frame is drawn.</summary>
    private void ScheduleGroupHeaders()
    {
        if (_groupHeadersPending) return;
        _groupHeadersPending = true;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(UpdateGroupHeaders, System.Windows.Threading.DispatcherPriority.DataBind);
    }

    private void UpdateGroupHeaders()
    {
        _groupHeadersPending = false;
        var rows = PlaylistView.Cast<LibraryFileViewModel>().ToList();
        if (_groupKey is not { } key)
        {
            foreach (var row in rows) row.GroupHeader = null;
            return;
        }
        var names = StringComparer.CurrentCultureIgnoreCase;
        var counts = new Dictionary<string, int>(names);
        foreach (var row in rows)
        {
            var name = key(row);
            counts[name] = counts.TryGetValue(name, out var n) ? n + 1 : 1;
        }
        string? previous = null;
        foreach (var row in rows)
        {
            var name = key(row);
            var starts = previous is null || !names.Equals(name, previous);
            row.GroupHeader = starts ? name : null;
            row.GroupCount = counts[name];
            previous = name;
        }
    }

    /// <summary>The queue order: by group (ascending - the order groups are listed in), then the
    /// chosen key in the chosen direction, then file name.</summary>
    private sealed class PlaylistOrder(Func<LibraryFileViewModel, string>? group, Comparison<LibraryFileViewModel> sort, bool descending)
        : System.Collections.IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not LibraryFileViewModel a || y is not LibraryFileViewModel b) return 0;
            if (group is not null)
            {
                var byGroup = StringComparer.CurrentCultureIgnoreCase.Compare(group(a), group(b));
                if (byGroup != 0) return byGroup;
            }
            var byKey = sort(a, b);
            if (byKey != 0) return descending ? -byKey : byKey;
            return StringComparer.CurrentCultureIgnoreCase.Compare(a.FileName, b.FileName);
        }
    }

    /// <summary>Re-reads row state after something (auto-fill) changed the underlying files'
    /// tags/cover/lyrics in place, and re-applies the 누락 filters to the new state.</summary>
    public void RefreshRows(IEnumerable<LibraryFile> changed)
    {
        var set = changed.ToHashSet();
        foreach (var vm in Files.Where(f => set.Contains(f.Model)))
            vm.Refresh();
        PlaylistView.Refresh();
        FilesView.Refresh();
    }

    /// <summary>Rescans when a merge/rename touched a folder the library is built from, so the
    /// list doesn't keep showing files that were moved or renamed away.</summary>
    public void RescanIfAffected(params string?[] changedFolders)
    {
        static string Norm(string p) => Path.GetFullPath(p).TrimEnd('\\', '/') + "\\";

        var changed = changedFolders.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => Norm(f!)).ToList();
        var affected = Folders.Any(source =>
        {
            var root = Norm(source.IsFile ? Path.GetDirectoryName(source.Path)! : source.Path);
            return changed.Any(c => c.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                                 || root.StartsWith(c, StringComparison.OrdinalIgnoreCase));
        });
        if (affected) RescanCommand.Execute(null);
    }

    /// <summary>Adds any mix of folders and individual audio files picked in one dialog
    /// (AlSong-style - see <see cref="FileSystemPicker.PickFoldersAndFiles"/>).</summary>
    [RelayCommand]
    private async Task AddFoldersAsync()
    {
        var picked = FileSystemPicker.PickFoldersAndFiles("추가할 음악 폴더/파일 선택 (여러 개 선택 가능)");
        if (picked is null || picked.Count == 0) return;

        var entries = new List<LibraryFolderEntry>();
        foreach (var path in picked)
        {
            var isFile = File.Exists(path);
            if (!isFile && !Directory.Exists(path)) continue; // vanished between picking and here
            entries.Add(new LibraryFolderEntry { Path = path, IncludeSubfolders = !isFile, IsFile = isFile });
        }
        await AddSourcesAsync(entries);
    }

    /// <summary>Registers new folders/files (local or from a network location), adding to the list
    /// or replacing it - asked, unless an answer was remembered - and rescans.</summary>
    public async Task AddSourcesAsync(IReadOnlyList<LibraryFolderEntry> entries)
    {
        if (entries.Count == 0) return;

        // Picking folders that are already registered (e.g. after a NAS was off) isn't a request
        // to change the list - it's "load them again".
        bool Registered(LibraryFolderEntry entry) => Folders.Any(f =>
            string.Equals(f.Path, entry.Path, StringComparison.OrdinalIgnoreCase) && f.Entry.RemoteSourceId == entry.RemoteSourceId);
        var fresh = entries.Where(e => !Registered(e)).ToList();
        Unexclude(entries);
        if (fresh.Count == 0)
        {
            Toast.Info("이미 등록된 항목입니다", entries.Count == 1 ? "목록을 다시 불러옵니다." : $"{entries.Count}개 모두 등록되어 있어 목록을 다시 불러옵니다.");
            await RescanAsync();
            return;
        }

        var mode = Folders.Count == 0 ? LibraryAddMode.Append : _settings.LibraryAddMode;
        if (mode == LibraryAddMode.Ask)
        {
            if (await Dialogs.ChooseAddModeAsync(Folders.Count, fresh.Count) is not { } choice) return;
            mode = choice.Mode;
            if (choice.Remember)
            {
                _settings.LibraryAddMode = choice.Mode;
                AppSettingsStore.Save(_settings);
                OnPropertyChanged(nameof(AddMode));
            }
        }

        var replaced = false;
        if (mode == LibraryAddMode.Replace)
        {
            Folders.Clear();
            replaced = true;
        }

        var added = false;
        foreach (var entry in entries)
        {
            if (Folders.Any(f => string.Equals(f.Path, entry.Path, StringComparison.OrdinalIgnoreCase)
                                 && f.Entry.RemoteSourceId == entry.RemoteSourceId)) continue;
            Folders.Add(new LibraryFolderViewModel(entry));
            added = true;
        }

        if (!added && !replaced) return;
        SaveFolders();
        await RescanAsync();
    }

    /// <summary>"네트워크 위치 추가": the setup wizard, then the chosen folder joins the library.</summary>
    [RelayCommand]
    private async Task AddNetworkLocationAsync()
    {
        var owner = System.Windows.Application.Current.Windows.OfType<System.Windows.Window>().FirstOrDefault(w => w.IsActive)
                    ?? System.Windows.Application.Current.MainWindow;
        if (Network.RemoteSourceWindow.Run(owner, _network) is { } folder)
            await AddSourcesAsync([folder]);
    }

    /// <summary>Network locations set up before (the + menu lists them).</summary>
    public IReadOnlyList<RemoteSource> SavedSources => _network.Sources;

    /// <summary>A saved location from the + menu: connects with its saved details and goes
    /// straight to picking a folder there.</summary>
    [RelayCommand]
    private async Task AddFromSavedSourceAsync(RemoteSource? source)
    {
        if (source is null) return;
        var owner = System.Windows.Application.Current.Windows.OfType<System.Windows.Window>().FirstOrDefault(w => w.IsActive)
                    ?? System.Windows.Application.Current.MainWindow;
        if (Network.RemoteSourceWindow.Run(owner, _network, source) is { } folder)
            await AddSourcesAsync([folder]);
    }

    /// <summary>A network location was removed: drop its folders/songs from the library.</summary>
    public async Task RemoveSourceAsync(string sourceId)
    {
        var gone = Folders.Where(f => f.Entry.RemoteSourceId == sourceId).ToList();
        if (gone.Count == 0) return;
        foreach (var f in gone) Folders.Remove(f);
        SaveFolders();
        await RescanAsync();
    }

    /// <summary>Settings › 폴더·파일을 추가할 때.</summary>
    public LibraryAddMode AddMode
    {
        get => _settings.LibraryAddMode;
        set
        {
            if (_settings.LibraryAddMode == value) return;
            _settings.LibraryAddMode = value;
            AppSettingsStore.Save(_settings);
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<Choice<LibraryAddMode>> AddModeOptions { get; } =
    [
        new(LibraryAddMode.Ask, "매번 묻기"),
        new(LibraryAddMode.Append, "기존 목록에 추가"),
        new(LibraryAddMode.Replace, "새로 등록"),
    ];

    public async void RemoveAllFolders()
    {
        if (Folders.Count == 0) return;
        Folders.Clear();
        SaveFolders();
        _excluded.Clear();
        SaveExcluded();
        await RescanAsync();
    }

    /// <summary>Checks existence off the UI thread (hundreds of lookups against an offline share can
    /// take minutes) and confirms first: a disconnected network drive makes every entry look
    /// missing, and silently dropping the whole list in that case would be a nasty surprise.</summary>
    public async void RemoveMissingFolders()
    {
        var snapshot = Folders.ToList();
        StatusText = "존재하지 않는 항목을 확인하는 중...";
        var missing = await Task.Run(() => snapshot.Where(f => !f.Exists).ToList());
        StatusText = $"{Files.Count}개 파일";

        if (missing.Count == 0)
        {
            Toast.Info("정리할 항목 없음", "등록된 폴더/파일이 모두 존재합니다.");
            return;
        }

        var allMissing = missing.Count == snapshot.Count;
        var confirmed = await Dialogs.ConfirmAsync("존재하지 않는 항목 제거",
            $"찾을 수 없는 폴더/파일 {missing.Count}개를 목록에서 제거합니다."
            + (allMissing ? "\n\n주의: 등록된 항목이 전부 보이지 않습니다. 네트워크 드라이브나 외장 디스크가 연결되지 않았을 수 있습니다." : "")
            + "\n실제 파일은 삭제되지 않습니다.",
            "제거");
        if (!confirmed) return;

        foreach (var f in missing) Folders.Remove(f);
        SaveFolders();
        await RescanAsync();
    }

    public async void RemoveDuplicateFolders()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new List<LibraryFolderViewModel>();
        foreach (var f in Folders)
        {
            if (!seen.Add(f.Path.TrimEnd('\\', '/')))
                duplicates.Add(f);
        }
        if (duplicates.Count == 0) return;
        foreach (var f in duplicates) Folders.Remove(f);
        SaveFolders();
        await RescanAsync();
    }

    private void SaveFolders()
    {
        _settings.LibraryFolders = [.. Folders.Select(f => f.Entry)];
        AppSettingsStore.Save(_settings);
    }

    /// <summary>Songs read last time, reused while unchanged (local and network-share folders).</summary>
    private readonly LibraryIndex _index = new(AppDataFolder.Combine("library-index.json"));

    [RelayCommand]
    private async Task RescanAsync()
    {
        _scanCts?.Cancel();
        var cts = new CancellationTokenSource();
        _scanCts = cts;

        Files.Clear();
        IsScanning = true;
        StatusText = "스캔 중...";
        var count = 0;
        var sources = Folders.ToList();

        // Songs reach the list in batches, a few times a second, built off the UI thread (their
        // thumbnails are decoded there) and added with one change notification per batch:
        // adding them one by one made the sorted, grouped views re-sort and the window redraw
        // for every song, freezing the player for seconds while a big library loaded.
        var pending = new List<LibraryFile>();
        var sinceFlush = System.Diagnostics.Stopwatch.StartNew();

        async Task Add(LibraryFile file)
        {
            if (_excluded.Count > 0 && _excluded.Contains(file.Path)) return; // taken out of the list
            pending.Add(file);
            count++;
            if (sinceFlush.ElapsedMilliseconds >= 300) await FlushAsync();
        }

        async Task FlushAsync()
        {
            sinceFlush.Restart();
            if (pending.Count == 0) return;
            var batch = pending.ToList();
            pending.Clear();
            var models = await Task.Run(() => batch.Select(f => new LibraryFileViewModel(f)).ToList());
            if (cts.IsCancellationRequested) return;
            Files.AddRange(models);
            StatusText = $"스캔 중... ({count}개)";
        }

        try
        {
            // Individually added files are read together in parallel (one at a time over a network
            // share was very slow); folders are walked and read in parallel by the scanner itself.
            // Samba shares with saved sign-in details are connected first (in the background).
            var smb = sources.Where(s => s.Entry.RemoteSourceId is not null && !s.Entry.IsRemote).ToList();
            if (smb.Count > 0) await Task.Run(() => smb.ForEach(s => _network.EnsureConnected(s.Entry)), cts.Token);

            var filePaths = sources.Where(s => s.IsFile)
                .Select(s => s.Entry.IsRemote ? RemotePath.Build(s.Entry.RemoteSourceId!, s.Path) : s.Path)
                .ToList();
            // Files on disk / a network share: unchanged ones come from the index. Files on a
            // WebDAV/FTP/SFTP source are read over the network.
            await foreach (var file in MediaLibraryScanner.ScanFilesAsync(filePaths.Where(p => !RemotePath.IsRemote(p)).ToList(), _index, cts.Token))
                await Add(file);
            await foreach (var file in MediaLibraryScanner.ScanFilesAsync(filePaths.Where(RemotePath.IsRemote).ToList(), cts.Token))
                await Add(file);
            await FlushAsync();
            var foundFromFileSources = count;
            var unreachableList = new List<LibraryFolderViewModel>();
            if (foundFromFileSources < filePaths.Count)
            {
                var found = Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                unreachableList.AddRange(sources.Where(s => s.IsFile && !found.Contains(
                    s.Entry.IsRemote ? RemotePath.Build(s.Entry.RemoteSourceId!, s.Path) : s.Path)));
            }

            var unreachableFolders = 0;
            foreach (var folder in sources.Where(s => !s.IsFile))
            {
                if (cts.IsCancellationRequested) break;

                if (folder.Entry.IsRemote)
                {
                    // WebDAV / FTP / SFTP: walked over the network, known songs come from the index.
                    var client = _network.ClientFor(folder.Entry.RemoteSourceId!);
                    if (client is null)
                    {
                        unreachableFolders++;
                        unreachableList.Add(folder);
                        continue;
                    }
                    StatusText = $"{_network.Find(folder.Entry.RemoteSourceId)?.Name} 읽는 중...";
                    try
                    {
                        await foreach (var file in MediaLibraryScanner.ScanRemoteAsync(client, folder.Entry.RemoteSourceId!,
                                           folder.Path, folder.Entry.IncludeSubfolders, _network.Index, cts.Token))
                            await Add(file);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        unreachableFolders++;
                        unreachableList.Add(folder);
                    }
                    continue;
                }

                var path = folder.Path;
                if (!await Task.Run(() => Directory.Exists(path), cts.Token))
                {
                    unreachableFolders++;
                    unreachableList.Add(folder);
                    continue;
                }

                await foreach (var file in MediaLibraryScanner.ScanAsync(path, folder.Entry.IncludeSubfolders, _index, cts.Token))
                    await Add(file);
            }
            await FlushAsync();
            _network.Index.Save();
            await Task.Run(_index.Save);

            if (!cts.IsCancellationRequested)
            {
                StatusText = BuildScanSummary(count, sources.Count, filePaths.Count, foundFromFileSources, unreachableFolders);
                var unreachable = unreachableFolders + Math.Max(0, filePaths.Count - foundFromFileSources);
                WatchForReconnect(unreachableList);
                // With nothing reachable at all the player itself explains it (and offers 다시 연결).
                if (unreachable > 0 && count > 0)
                    Toast.Warning("일부 항목에 접근할 수 없습니다",
                        $"{unreachable}개 폴더/파일을 읽지 못했습니다. 네트워크 드라이브나 외장 디스크 연결을 확인해 주세요.");
            }
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer scan - nothing to report
        }
        catch (Exception ex)
        {
            // Never end silently with a half-filled list.
            App.LogError(ex);
            StatusText = $"{count}개 파일 (스캔 중 오류: {ex.Message})";
            Toast.Error("음악 목록을 끝까지 읽지 못했습니다", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_scanCts, cts)) IsScanning = false;
        }
    }

    /// <summary>Explains an empty/partial result instead of a bare "0개" - most often it's a network
    /// share that isn't connected, not files that are actually gone.</summary>
    private static string BuildScanSummary(int found, int sourceCount, int fileSourceCount, int foundFromFileSources, int unreachableFolders)
    {
        if (sourceCount == 0) return "폴더를 추가해 주세요.";

        var unreadableFiles = Math.Max(0, fileSourceCount - foundFromFileSources);
        var problems = unreachableFolders + unreadableFiles;

        if (found == 0)
            return problems > 0
                ? "등록된 폴더/파일에 접근할 수 없습니다. 네트워크 드라이브나 외장 디스크 연결을 확인해 주세요."
                : "음원 파일을 찾지 못했습니다.";

        return problems > 0 ? $"{found}개 파일 (접근할 수 없는 항목 {problems}개)" : $"{found}개 파일";
    }
}
