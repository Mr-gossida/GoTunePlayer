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

    public ObservableCollection<LibraryFileViewModel> Files { get; } = [];
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

    public LibraryViewModel(AppSettings settings)
    {
        _settings = settings;
        FilesView = CollectionViewSource.GetDefaultView(Files);
        FilesView.Filter = o => o is LibraryFileViewModel vm && Filter.Matches(vm);
        // Files stream in from parallel readers in no particular order; keep the list (and so
        // the playlist and next/previous order) stable and predictable.
        FilesView.SortDescriptions.Add(new SortDescription(nameof(LibraryFileViewModel.FileName), ListSortDirection.Ascending));
        Filter.FilterChanged += (_, _) => FilesView.Refresh();

        PlaylistView = new ListCollectionView(Files);
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
        var groupProperty = PlaylistGroup switch
        {
            PlaylistGroupKey.Folder => nameof(LibraryFileViewModel.Folder),
            PlaylistGroupKey.Artist => nameof(LibraryFileViewModel.ArtistGroup),
            PlaylistGroupKey.Album => nameof(LibraryFileViewModel.AlbumGroup),
            _ => null,
        };
        var sortProperty = PlaylistSort switch
        {
            PlaylistSortKey.Path => nameof(LibraryFileViewModel.Path),
            PlaylistSortKey.Artist => nameof(LibraryFileViewModel.Artist),
            PlaylistSortKey.Title => nameof(LibraryFileViewModel.DisplayTitle),
            PlaylistSortKey.Album => nameof(LibraryFileViewModel.Album),
            PlaylistSortKey.Duration => nameof(LibraryFileViewModel.Duration),
            _ => nameof(LibraryFileViewModel.FileName),
        };
        var direction = PlaylistSortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending;

        using (PlaylistView.DeferRefresh())
        {
            PlaylistView.GroupDescriptions.Clear();
            PlaylistView.SortDescriptions.Clear();
            if (groupProperty is not null)
            {
                PlaylistView.GroupDescriptions.Add(new PropertyGroupDescription(groupProperty));
                PlaylistView.SortDescriptions.Add(new SortDescription(groupProperty, ListSortDirection.Ascending));
            }
            PlaylistView.SortDescriptions.Add(new SortDescription(sortProperty, direction));
            if (sortProperty != nameof(LibraryFileViewModel.FileName))
                PlaylistView.SortDescriptions.Add(new SortDescription(nameof(LibraryFileViewModel.FileName), ListSortDirection.Ascending));
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

        // Add to the current list, or replace it (asked, unless an answer was remembered).
        var mode = Folders.Count == 0 ? LibraryAddMode.Append : _settings.LibraryAddMode;
        if (mode == LibraryAddMode.Ask)
        {
            if (await Dialogs.ChooseAddModeAsync(Folders.Count, picked.Count) is not { } choice) return;
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
        foreach (var path in picked)
        {
            if (Folders.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))) continue;

            var isFile = File.Exists(path);
            if (!isFile && !Directory.Exists(path)) continue; // vanished between picking and here

            Folders.Add(new LibraryFolderViewModel(new LibraryFolderEntry
            {
                Path = path,
                IncludeSubfolders = !isFile,
                IsFile = isFile,
            }));
            added = true;
        }

        if (!added && !replaced) return;
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

        void Add(LibraryFile file)
        {
            Files.Add(new LibraryFileViewModel(file));
            count++;
            if (count % 20 == 0) StatusText = $"스캔 중... ({count}개)";
        }

        try
        {
            // Individually added files are read together in parallel (one at a time over a network
            // share was very slow); folders are walked and read in parallel by the scanner itself.
            var filePaths = sources.Where(s => s.IsFile).Select(s => s.Path).ToList();
            await foreach (var file in MediaLibraryScanner.ScanFilesAsync(filePaths, cts.Token))
                Add(file);
            var foundFromFileSources = count;

            var unreachableFolders = 0;
            foreach (var folder in sources.Where(s => !s.IsFile))
            {
                if (cts.IsCancellationRequested) break;
                var path = folder.Path;
                if (!await Task.Run(() => Directory.Exists(path), cts.Token))
                {
                    unreachableFolders++;
                    continue;
                }

                await foreach (var file in MediaLibraryScanner.ScanAsync(path, folder.Entry.IncludeSubfolders, cts.Token))
                    Add(file);
            }

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
