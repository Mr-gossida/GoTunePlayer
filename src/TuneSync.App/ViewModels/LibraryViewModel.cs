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
        FilesView.Filter = o => o is LibraryFileViewModel vm && InScope(vm) && Filter.Matches(vm);
        // Files stream in from parallel readers in no particular order; keep the list (and so
        // the playlist and next/previous order) stable and predictable.
        FilesView.SortDescriptions.Add(new SortDescription(nameof(LibraryFileViewModel.FileName), ListSortDirection.Ascending));
        Filter.FilterChanged += (_, _) => FilesView.Refresh();

        PlaylistView = new ListCollectionView(Files) { Filter = o => o is not LibraryFileViewModel { IsFoldedAway: true } };
        ((System.Collections.Specialized.INotifyCollectionChanged)PlaylistView).CollectionChanged += (_, _) => ScheduleGroupHeaders();
        _playlistSort = settings.PlaylistSort;
        _playlistSortDescending = settings.PlaylistSortDescending;
        _playlistGroup = settings.PlaylistGroup;
        ApplyPlaylistOrder();

        Files.CollectionChanged += (_, _) => NotifyLibraryState();
        Folders.CollectionChanged += (_, _) =>
        {
            NotifyLibraryState();
            FolderTreeChanged();
        };

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

    /// <summary>Raised after a scan put a batch of rows in the list. A rescan builds a new row for
    /// every song, including the ones that were there before, so whoever holds an old row (the
    /// playing song, "다음에 재생") can move over to the new row of the same file.</summary>
    public event Action<IReadOnlyList<LibraryFileViewModel>>? FilesAdded;

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

    // ---- 태그 관리 scope: only the songs picked in the player's queue ----

    private HashSet<string>? _scope; // by path: a rescan rebuilds the rows

    /// <summary>"재생 대기열에서 고른 14곡" while the tag manager shows only those songs; null for
    /// the whole library.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScope))]
    private string? _scopeLabel;

    public bool HasScope => ScopeLabel is not null;

    private bool InScope(LibraryFileViewModel file) => _scope is null || _scope.Contains(file.Path);

    /// <summary>Shows only <paramref name="files"/> in the tag manager's list.</summary>
    public void SetScope(IReadOnlyCollection<LibraryFileViewModel> files, string label)
    {
        _scope = new HashSet<string>(files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        ScopeLabel = label;
        FilesView.Refresh();
    }

    /// <summary>Back to the whole library (the scope bar's "전체 보기").</summary>
    [RelayCommand]
    private void ClearScope()
    {
        if (_scope is null) return;
        _scope = null;
        ScopeLabel = null;
        FilesView.Refresh();
    }

    partial void OnPlaylistSortChanged(PlaylistSortKey value) => SavePlaylistOrder();
    partial void OnPlaylistSortDescendingChanged(bool value) => SavePlaylistOrder();
    partial void OnPlaylistGroupChanged(PlaylistGroupKey value)
    {
        _foldedGroups.Clear(); // folds name groups of the old kind (a folder, an artist...)
        SavePlaylistOrder();
    }

    private bool _holdOrder;
    private Dictionary<string, int> _customRank = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Drag and drop in the play list: the songs go right before <paramref name="before"/>
    /// (or to the end). The list switches to 직접 정렬 - no groups, the dragged order saved - and
    /// says so the first time.</summary>
    public void MoveInQueue(IReadOnlyList<LibraryFileViewModel> moving, LibraryFileViewModel? before)
    {
        if (moving.Count == 0) return;
        var order = QueueOrder();
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
        _chains.Clear(); // the library's folders may have changed since
        Func<LibraryFileViewModel, string>? group = PlaylistGroup switch
        {
            PlaylistGroupKey.Folder => f => f.Folder,
            PlaylistGroupKey.Artist => f => f.ArtistGroup,
            PlaylistGroupKey.Album => f => f.AlbumGroup,
            _ => null,
        };
        Comparison<LibraryFileViewModel>? groupOrder = PlaylistGroup switch
        {
            // as a tree: a folder's own songs, then its subfolders, level by level
            PlaylistGroupKey.Folder => (a, b) => CompareChains(ChainOf(a), ChainOf(b)),
            PlaylistGroupKey.None => null,
            _ => (a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(group!(a), group!(b)),
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
            PlaylistView.CustomSort = new PlaylistOrder(groupOrder, sort, PlaylistSortDescending);
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
        var rows = QueueOrder();
        var foldsChanged = false;
        if (_groupKey is not { } key)
        {
            foreach (var row in rows)
            {
                row.GroupHeader = null;
                SetHeadings(row, null);
                row.RowIndent = FlatIndent;
                row.GroupName = null;
                row.IsGroupFolded = false;
                foldsChanged |= row.IsFoldedAway;
                row.IsFoldedAway = false;
            }
            _foldedAwayCount = 0;
            if (foldsChanged) PlaylistView.Refresh();
            return;
        }
        if (PlaylistGroup == PlaylistGroupKey.Folder)
        {
            UpdateFolderTree(rows);
            return;
        }
        var groupIcon = PlaylistGroup == PlaylistGroupKey.Artist ? Wpf.Ui.Controls.SymbolRegular.Person24 : Wpf.Ui.Controls.SymbolRegular.MusicNote224;
        var names = StringComparer.CurrentCultureIgnoreCase;
        var counts = new Dictionary<string, int>(names);
        foreach (var row in rows)
        {
            var name = key(row);
            counts[name] = counts.TryGetValue(name, out var n) ? n + 1 : 1;
        }
        string? previous = null;
        _foldedAwayCount = 0;
        foreach (var row in rows)
        {
            var name = key(row);
            var starts = previous is null || !names.Equals(name, previous);
            row.GroupHeader = starts ? name : null;
            row.GroupName = name;
            row.GroupLabel = name;
            row.GroupCount = counts[name];
            var folded = _foldingAllowed && _foldedGroups.Contains(name);
            SetHeadings(row, starts ? [new GroupHeading(name, name, 0, counts[name], folded, groupIcon)] : null);
            row.RowIndent = FlatIndent;
            row.IsGroupFolded = folded;
            var away = folded && !starts;
            foldsChanged |= row.IsFoldedAway != away;
            row.IsFoldedAway = away;
            if (away) _foldedAwayCount++;
            previous = name;
        }
        // The filter reads IsFoldedAway; re-run it only when that changed (the refresh comes back
        // here once, finds nothing new, and stops).
        if (foldsChanged) PlaylistView.Refresh();
    }

    // ---- folding groups in the queue (click a group's heading) ----

    private readonly HashSet<string> _foldedGroups = new(StringComparer.CurrentCultureIgnoreCase);
    private bool _foldingAllowed = true;
    private int _foldedAwayCount; // rows the filter leaves out right now (they aren't in the view to count)

    /// <summary>Off in the cover grid: it draws no headings, so a folded group couldn't be
    /// opened again there. The folds come back when the list returns.</summary>
    public bool FoldingAllowed
    {
        get => _foldingAllowed;
        set
        {
            if (_foldingAllowed == value) return;
            _foldingAllowed = value;
            if (_foldedGroups.Count > 0) UpdateGroupHeaders();
        }
    }

    /// <summary>Folds or unfolds a group of the queue: a folded group shows only its heading.</summary>
    public void ToggleGroupFold(string group)
    {
        if (!_foldedGroups.Remove(group)) _foldedGroups.Add(group);
        UpdateGroupHeaders();
    }

    /// <summary>Unfolds the group a song is in, so it can be shown (search hit, 지금 재생 중).
    /// True when something was unfolded.</summary>
    public bool UnfoldGroupOf(LibraryFileViewModel song)
    {
        if (!song.IsFoldedAway && !song.IsGroupFolded) return false;
        var removed = false;
        if (PlaylistGroup == PlaylistGroupKey.Folder)
        {
            // every folded folder above it, down to its own
            foreach (var node in ChainOf(song)) removed |= _foldedGroups.Remove(node.Key);
        }
        else if (song.GroupName is { } group)
        {
            removed = _foldedGroups.Remove(group);
        }
        if (removed) UpdateGroupHeaders();
        return removed;
    }

    // ---- folders as a tree (그룹 › 폴더별) ----

    /// <summary>A folder level of the tree: <see cref="Key"/> tells it apart (the whole path),
    /// <see cref="Name"/> is what its heading says.</summary>
    private sealed record FolderNode(string Key, string Name, int Depth);

    /// <summary>Each folder's levels, worked out once per refresh (the sort reads them thousands
    /// of times).</summary>
    private readonly Dictionary<string, FolderNode[]> _chains = new(StringComparer.OrdinalIgnoreCase);

    private bool _treeRefreshQueued;

    /// <summary>The library folders changed: the tree hangs off them (a song added on its own sits
    /// under a library folder added later), so it's worked out anew - once, after a run of changes.
    /// Until the sort or grouping was changed, songs kept their old place in the tree, and
    /// next/previous followed that order.</summary>
    private void FolderTreeChanged()
    {
        _chains.Clear();
        if (PlaylistGroup != PlaylistGroupKey.Folder || _treeRefreshQueued) return;
        _treeRefreshQueued = true;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _treeRefreshQueued = false;
            if (PlaylistGroup == PlaylistGroupKey.Folder) ApplyPlaylistOrder();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private static readonly Thickness FlatIndent = new(0, 1, 12, 1);

    /// <summary>How far each level is set in.</summary>
    private const double TreeStep = 16;

    /// <summary>The folder levels a song sits under: the library folder it was added from (its
    /// heading the folder's own name, like 알송's ETC), then each subfolder down to the song's
    /// own. A song added on its own, or from outside every library folder, sits under its
    /// folder alone; a network song under its server, then the folders on it.</summary>
    private FolderNode[] ChainOf(LibraryFileViewModel song)
    {
        var folder = song.Folder;
        if (_chains.TryGetValue(folder, out var chain)) return chain;

        var names = new List<string>();
        string rootKey;
        if (RemotePath.TryParse(song.Path, out var sourceId, out var remotePath))
        {
            rootKey = "remote:" + sourceId;
            names.Add(LibraryFileViewModel.RemoteSourceName?.Invoke(sourceId) ?? "네트워크");
            var dir = remotePath[..Math.Max(0, remotePath.LastIndexOf('/'))];
            names.AddRange(dir.Split('/', StringSplitOptions.RemoveEmptyEntries));
        }
        else
        {
            var dir = folder.TrimEnd('\\', '/');
            string? root = null;
            foreach (var source in Folders)
            {
                if (source.IsFile || source.Entry.IsRemote) continue;
                var path = source.Path.TrimEnd('\\', '/');
                if ((dir.Equals(path, StringComparison.OrdinalIgnoreCase) || dir.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase))
                    && (root is null || path.Length > root.Length))
                    root = path;
            }
            root ??= dir;
            rootKey = root;
            names.Add(LeafName(root));
            names.AddRange(dir[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries));
        }

        chain = new FolderNode[names.Count];
        var key = rootKey;
        for (var i = 0; i < names.Count; i++)
        {
            if (i > 0) key += "\\" + names[i];
            chain[i] = new FolderNode(key, names[i], i);
        }
        _chains[folder] = chain;
        return chain;
    }

    /// <summary>A folder's own name: its last part (a share's root \\NAS\Music reads "Music",
    /// a drive D: names itself).</summary>
    private static string LeafName(string path)
    {
        var parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : path;
    }

    /// <summary>Tree order: level by level by name; a folder's own songs before its subfolders.</summary>
    private static int CompareChains(FolderNode[] a, FolderNode[] b)
    {
        if (ReferenceEquals(a, b)) return 0;
        var names = StringComparer.CurrentCultureIgnoreCase;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            if (string.Equals(a[i].Key, b[i].Key, StringComparison.OrdinalIgnoreCase)) continue;
            var byName = names.Compare(a[i].Name, b[i].Name);
            return byName != 0 ? byName : string.Compare(a[i].Key, b[i].Key, StringComparison.OrdinalIgnoreCase);
        }
        return a.Length.CompareTo(b.Length);
    }

    /// <summary>The headings of the folder tree: above each song, a heading for every folder
    /// level that starts there, set in by its depth; the songs set in under their own folder.
    /// A folded level shows its heading alone - everything under it, subfolders too, is left out.</summary>
    private void UpdateFolderTree(List<LibraryFileViewModel> rows)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
            foreach (var node in ChainOf(row))
                counts[node.Key] = counts.TryGetValue(node.Key, out var n) ? n + 1 : 1;

        var foldsChanged = false;
        _foldedAwayCount = 0;
        FolderNode[]? previous = null;
        var headings = new List<GroupHeading>();
        foreach (var row in rows)
        {
            var chain = ChainOf(row);
            // the levels this song shares with the one before; the rest start here
            var common = 0;
            if (previous is not null)
                while (common < Math.Min(chain.Length, previous.Length)
                       && string.Equals(chain[common].Key, previous[common].Key, StringComparison.OrdinalIgnoreCase))
                    common++;
            previous = chain;

            // the highest folded level above (or at) this song's folder
            var foldAt = -1;
            if (_foldingAllowed)
                for (var i = 0; i < chain.Length && foldAt < 0; i++)
                    if (_foldedGroups.Contains(chain[i].Key)) foldAt = i;

            var last = foldAt >= 0 ? foldAt : chain.Length - 1;
            var away = foldAt >= 0 && common > foldAt; // inside a folded level, not its first song
            headings.Clear();
            if (!away)
                for (var i = common; i <= last; i++)
                {
                    var folded = _foldingAllowed && _foldedGroups.Contains(chain[i].Key);
                    headings.Add(new GroupHeading(chain[i].Key, chain[i].Name, i, counts[chain[i].Key], folded,
                        folded ? Wpf.Ui.Controls.SymbolRegular.Folder24 : Wpf.Ui.Controls.SymbolRegular.FolderOpen24));
                }

            var own = chain[^1];
            // The group the row is shown under: its own folder, or the folded level above that hides
            // it. (Its own deepest folder had the pinned heading name - and fold - a folder that
            // wasn't on screen.)
            var shown = foldAt >= 0 ? chain[foldAt] : own;
            row.GroupName = shown.Key;
            row.GroupLabel = shown.Name;
            row.GroupCount = counts[shown.Key];
            row.GroupHeader = headings.Count > 0 ? headings[^1].Name : null;
            SetHeadings(row, headings.Count > 0 ? [.. headings] : null);
            row.RowIndent = new Thickness(TreeStep * own.Depth, 1, 12, 1);
            row.IsGroupFolded = foldAt >= 0 && !away;
            foldsChanged |= row.IsFoldedAway != away;
            row.IsFoldedAway = away;
            if (away) _foldedAwayCount++;
        }
        if (foldsChanged) PlaylistView.Refresh();
    }

    /// <summary>Sets a row's headings only when they differ (a new list re-draws them).</summary>
    private static void SetHeadings(LibraryFileViewModel row, IReadOnlyList<GroupHeading>? headings)
    {
        var now = row.Headings;
        if (now is null ? headings is null : headings is not null && now.SequenceEqual(headings)) return;
        row.Headings = headings;
    }

    /// <summary>Every song of the queue in its order, folded groups included: what playback,
    /// search and drag-and-drop go by (<see cref="PlaylistView"/> leaves folded songs out).</summary>
    public List<LibraryFileViewModel> QueueOrder()
    {
        if (_foldedAwayCount == 0) return [.. PlaylistView.Cast<LibraryFileViewModel>()];
        var all = Files.ToList();
        if (PlaylistView.CustomSort is { } order) all.Sort(order.Compare);
        return all;
    }

    /// <summary>The queue order: by group (ascending - the order groups are listed in), then the
    /// chosen key in the chosen direction, then file name.</summary>
    private sealed class PlaylistOrder(Comparison<LibraryFileViewModel>? group, Comparison<LibraryFileViewModel> sort, bool descending)
        : System.Collections.IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not LibraryFileViewModel a || y is not LibraryFileViewModel b) return 0;
            if (group is not null)
            {
                var byGroup = group(a, b);
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

    /// <summary>Songs renamed one at a time (the tag editor's 파일 이름 바꾸기): their rows, a file
    /// registered on its own and the saved custom order follow the new names in place. Rescanning
    /// instead would rebuild every row - the editor's window holds the old ones - and read the
    /// whole library again for one name.</summary>
    public void ApplyRenames(IReadOnlyList<(string From, string To)> moves)
    {
        var map = moves.Where(m => TagService.IsSupported(m.To))
            .GroupBy(m => m.From, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().To, StringComparer.OrdinalIgnoreCase);
        if (map.Count == 0) return;

        foreach (var row in Files)
            if (!row.IsRemote && map.TryGetValue(row.Path, out var to)) row.MoveTo(to);

        var sourcesMoved = false;
        foreach (var source in Folders.Where(f => f.IsFile && !f.Entry.IsRemote))
        {
            if (!map.TryGetValue(source.Path, out var to)) continue;
            source.MoveTo(to);
            sourcesMoved = true;
        }
        if (sourcesMoved) SaveFolders();

        if (_settings.CustomOrder.Any(map.ContainsKey))
        {
            _settings.CustomOrder = [.. _settings.CustomOrder.Select(p => map.TryGetValue(p, out var to) ? to : p)];
            AppSettingsStore.Save(_settings);
        }
        // (the custom order in use keeps the song in its place, not at the end as a newcomer)
        foreach (var (from, to) in map)
            if (_customRank.Remove(from, out var rank)) _customRank[to] = rank;

        PlaylistView.Refresh();
        FilesView.Refresh();
    }

    /// <summary>Rescans when a merge/rename touched a folder the library is built from, so the
    /// list doesn't keep showing files that were moved or renamed away.</summary>
    public void RescanIfAffected(params string?[] changedFolders)
    {
        static string Norm(string p) => Path.GetFullPath(p).TrimEnd('\\', '/') + "\\";

        var changed = changedFolders.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => Norm(f!)).ToList();
        // (A network location's folder "/Music" isn't a path on this PC: read as one it became
        // C:\Music\ and set off a full rescan - the network too - for changes elsewhere.)
        var affected = Folders.Where(source => !source.Entry.IsRemote).Any(source =>
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
        if (fresh.Count == 0)
        {
            Unexclude(entries);
            Toast.Info("이미 등록된 항목입니다", entries.Count == 1 ? "목록을 다시 불러옵니다." : $"{entries.Count}개 모두 등록되어 있어 목록을 다시 불러옵니다.");
            await RescanAsync();
            return;
        }

        var mode = Folders.Count == 0 ? LibraryAddMode.Append : _settings.LibraryAddMode;
        if (mode == LibraryAddMode.Ask)
        {
            // Cancelled: nothing changes - the songs taken out of the list stay out (they used to
            // be put back and saved before the question was even answered).
            if (await Dialogs.ChooseAddModeAsync(Folders.Count, fresh.Count) is not { } choice) return;
            mode = choice.Mode;
            if (choice.Remember)
            {
                _settings.LibraryAddMode = choice.Mode;
                AppSettingsStore.Save(_settings);
                OnPropertyChanged(nameof(AddMode));
            }
        }
        var unexcluded = Unexclude(entries);

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

        if (!added && !replaced)
        {
            if (unexcluded) await RescanAsync(); // songs put back in still have to come back to the list
            return;
        }
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
            // the same folder of the same place: "/Music" on two servers is two folders
            if (!seen.Add((f.Entry.RemoteSourceId ?? "") + "|" + f.Path.TrimEnd('\\', '/')))
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

    /// <summary>Rows with unsaved tag edits that a rescan replaced, by path, until the new row of
    /// the same file arrives (the edits live only on the rows). Kept across a scan that a newer
    /// one replaced before it got to them.</summary>
    private readonly Dictionary<string, LibraryFileViewModel> _carriedEdits = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Songs whose unsaved edits wait for a rescan to bring their rows back. The tag
    /// editor doesn't count them meanwhile, so whoever asks before they're lost (closing the app)
    /// should.</summary>
    public int PendingEditsInScan => _carriedEdits.Count;

    /// <summary>Hands the unsaved edits waiting in <see cref="_carriedEdits"/> to the new rows of
    /// the same files. On the UI thread: taking them over updates the tag editor.</summary>
    private void CarryEdits(IReadOnlyList<LibraryFileViewModel> rows)
    {
        if (_carriedEdits.Count == 0) return;
        foreach (var row in rows)
            if (_carriedEdits.Remove(row.Path, out var old)) row.TakePendingFrom(old);
    }

    /// <summary>After a scan that read everything: edits whose song didn't come back can't be
    /// kept - say so rather than lose them silently.</summary>
    private void ReportUncarriedEdits()
    {
        if (_carriedEdits.Count == 0) return;
        var count = _carriedEdits.Count;
        _carriedEdits.Clear();
        Toast.Warning("저장하지 않은 변경 내용이 사라졌습니다",
            $"태그 변경을 저장하지 않은 {count}곡을 다시 불러온 목록에서 찾지 못했습니다. 파일이 옮겨졌거나 지금 접근할 수 없는 위치에 있을 수 있습니다.");
    }

    [RelayCommand]
    private async Task RescanAsync()
    {
        _scanCts?.Cancel();
        var cts = new CancellationTokenSource();
        _scanCts = cts;

        // Every row is about to be rebuilt: unsaved edits wait for the new row of the same file.
        foreach (var file in Files)
            if (file.HasPendingChanges) _carriedEdits[file.Path] = file;
        Files.Clear();
        IsScanning = true;
        StatusText = "스캔 중...";
        _index.BeginScan();
        _network.Index.BeginScan();
        var count = 0;
        var sources = Folders.ToList();

        // Songs reach the list in batches, a few times a second, built off the UI thread (their
        // thumbnails are decoded there) and added with one change notification per batch:
        // adding them one by one made the sorted, grouped views re-sort and the window redraw
        // for every song, freezing the player for seconds while a big library loaded.
        var pending = new List<LibraryFile>();
        var sinceFlush = System.Diagnostics.Stopwatch.StartNew();
        // Sources can overlap (a folder and one inside it, a file in a registered folder): each song
        // once. It used to be listed twice - and taking one row out of the list left the other.
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        async Task Add(LibraryFile file)
        {
            if (_excluded.Count > 0 && _excluded.Contains(file.Path)) return; // taken out of the list
            if (!listed.Add(file.Path)) return;
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
            CarryEdits(models);
            Files.AddRange(models);
            FilesAdded?.Invoke(models);
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
            // Every folder was reached and read to the end: songs the indexes hold that this scan
            // didn't meet are gone (or out of the library) and are dropped. (With a folder offline,
            // its songs stay remembered for when it's back.)
            if (!cts.IsCancellationRequested && unreachableFolders == 0 && foundFromFileSources == filePaths.Count)
            {
                _index.ForgetUnmet();
                _network.Index.ForgetUnmet();
            }
            // (written off the UI thread: the network index holds a thumbnail per song)
            await Task.Run(_network.Index.Save);
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
                ReportUncarriedEdits();
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
