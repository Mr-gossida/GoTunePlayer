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

        Files.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoFiles));

        foreach (var entry in settings.LibraryFolders)
            Folders.Add(new LibraryFolderViewModel(entry));

        if (Folders.Count > 0) _ = RescanAsync();
    }

    public bool HasNoFiles => Files.Count == 0;

    partial void OnPlaylistSortChanged(PlaylistSortKey value) => SavePlaylistOrder();
    partial void OnPlaylistSortDescendingChanged(bool value) => SavePlaylistOrder();
    partial void OnPlaylistGroupChanged(PlaylistGroupKey value) => SavePlaylistOrder();

    [RelayCommand]
    private void TogglePlaylistSortDirection() => PlaylistSortDescending = !PlaylistSortDescending;

    private void SavePlaylistOrder()
    {
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
            _ => (a, b) => text.Compare(a.FileName, b.FileName),
        };

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

        var mode = Folders.Count == 0 ? LibraryAddMode.Append : _settings.LibraryAddMode;
        if (mode == LibraryAddMode.Ask)
        {
            if (await Dialogs.ChooseAddModeAsync(Folders.Count, entries.Count) is not { } choice) return;
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
    private readonly LibraryIndex _index = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TuneSync", "library-index.json"));

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
                    }
                    continue;
                }

                var path = folder.Path;
                if (!await Task.Run(() => Directory.Exists(path), cts.Token))
                {
                    unreachableFolders++;
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
                if (unreachable > 0)
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
