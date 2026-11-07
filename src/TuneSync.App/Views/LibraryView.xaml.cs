using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.App.Views;

/// <summary>Unified replacement for the old Cover/Rename/Lyrics/Tag pages: an Explorer-style
/// folder tree on the left drives a file list on the right, and selecting files there switches
/// on the matching task tab. All four tabs are thin UI wrappers over the same TuneSync.Core
/// services the old pages used (CoverService/RenameService/LyricsService/TagService).</summary>
public partial class LibraryView : Page
{
    private readonly ObservableCollection<LibraryFileItem> _files = [];
    private readonly ObservableCollection<RenameRow> _renamePreview = [];
    private readonly ObservableCollection<LibraryFileItem> _lyricsResults = [];
    private readonly LyricsService _lyricsService = new();
    private string? _currentFolder;
    private bool _isGridMode;
    private List<(string OldPath, string NewPath)> _renameApplyList = [];

    public LibraryView()
    {
        InitializeComponent();
        FolderTree.ItemsSource = FolderNode.CreateRoots();
        FileListGrid.ItemsSource = _files;
        FileGridBox.ItemsSource = _files;
        RenamePreviewGrid.ItemsSource = _renamePreview;
        LyricsResultGrid.ItemsSource = _lyricsResults;
        LoadRulesIntoForm(AppSettings.LoadRenameRules());
        Unloaded += (_, _) => _lyricsService.Dispose();
        UpdateTaskPanel();
    }

    // ----- Folder tree -----

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "열 폴더 선택" };
        if (dlg.ShowDialog() != true) return;
        _currentFolder = dlg.FolderName;
        FolderTree.ItemsSource = new ObservableCollection<FolderNode> { new(dlg.FolderName, dlg.FolderName) };
        LoadCurrentFolder();
    }

    private void TreeViewItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { DataContext: FolderNode node })
            node.EnsureLoaded();
    }

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not FolderNode node) return;
        _currentFolder = node.Path;
        LoadCurrentFolder();
    }

    private void IncludeSubfolders_Changed(object sender, RoutedEventArgs e) => LoadCurrentFolder();

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadCurrentFolder();

    private void LoadCurrentFolder()
    {
        _files.Clear();
        if (_currentFolder is null || !Directory.Exists(_currentFolder))
        {
            PathText.Text = "왼쪽에서 폴더를 선택하세요";
            UpdateTaskPanel();
            return;
        }

        var option = IncludeSubfoldersCheck.IsChecked == true ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        IEnumerable<string> paths;
        try { paths = Directory.EnumerateFiles(_currentFolder, "*", option).Where(TagService.IsSupported); }
        catch { paths = []; }

        foreach (var path in paths)
        {
            try
            {
                var tags = TagService.Read(path);
                var cover = CoverService.GetCover(path);
                var lyrics = TagService.ReadLyrics(path);
                _files.Add(new LibraryFileItem(path, tags, cover, !string.IsNullOrWhiteSpace(lyrics)));
            }
            catch
            {
                // unreadable tag header etc. - skip rather than abort the whole scan
            }
        }

        PathText.Text = _currentFolder;
        UpdateTaskPanel();
    }

    // ----- List/Grid toggle -----

    private void ListModeRadio_Checked(object sender, RoutedEventArgs e) => SetViewMode(gridMode: false);

    private void GridModeRadio_Checked(object sender, RoutedEventArgs e) => SetViewMode(gridMode: true);

    private void SetViewMode(bool gridMode)
    {
        if (_isGridMode == gridMode) return;
        var selected = GetSelectedItems();
        _isGridMode = gridMode;
        FileListGrid.Visibility = gridMode ? Visibility.Collapsed : Visibility.Visible;
        FileGridBox.Visibility = gridMode ? Visibility.Visible : Visibility.Collapsed;

        if (gridMode)
        {
            FileGridBox.SelectedItems.Clear();
            foreach (var item in selected) FileGridBox.SelectedItems.Add(item);
        }
        else
        {
            FileListGrid.SelectedItems.Clear();
            foreach (var item in selected) FileListGrid.SelectedItems.Add(item);
        }
    }

    private List<LibraryFileItem> GetSelectedItems() =>
        (_isGridMode ? FileGridBox.SelectedItems : FileListGrid.SelectedItems).Cast<LibraryFileItem>().ToList();

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateTaskPanel();

    // ----- Task panel sync -----

    private void UpdateTaskPanel()
    {
        var selected = GetSelectedItems();
        var hasSelection = selected.Count > 0;
        var first = selected.FirstOrDefault();

        CoverPreviewImage.Source = first?.Thumbnail;
        CoverSelectionInfo.Text = selected.Count switch
        {
            0 => "파일을 선택하면 커버를 확인하고 바꿀 수 있습니다.",
            1 => first!.FileName,
            _ => $"{selected.Count}개 선택됨 (미리보기는 첫 항목 기준)",
        };

        TagEditGrid.ItemsSource = hasSelection ? selected : _files.ToList();
        TagStatusText.Text = hasSelection
            ? $"선택한 {selected.Count}개 파일을 편집합니다."
            : $"선택된 파일이 없어 현재 폴더의 전체 {_files.Count}개 파일을 편집합니다.";

        RenameTargetInfo.Text = hasSelection
            ? $"선택한 {selected.Count}개 파일에 적용합니다."
            : $"선택된 파일이 없어 현재 폴더의 전체 {_files.Count}개 파일에 적용합니다.";

        LyricsTargetInfo.Text = hasSelection
            ? $"선택한 {selected.Count}개 파일 기준으로 검색/적용합니다."
            : $"선택된 파일이 없어 전체 {_files.Count}개 파일 기준으로 검색/적용합니다.";
    }

    private List<LibraryFileItem> GetTaskTargets()
    {
        var selected = GetSelectedItems();
        return selected.Count > 0 ? selected : _files.ToList();
    }

    // ----- Cover tab -----

    private void ReplaceCover_Click(object sender, RoutedEventArgs e)
    {
        var first = GetSelectedItems().FirstOrDefault();
        if (first is null)
        {
            MessageBox.Show("먼저 파일을 선택해 주세요.", "TuneSync");
            return;
        }

        var dlg = new OpenFileDialog { Filter = "이미지 파일|*.jpg;*.jpeg;*.png", Title = "새 커버 이미지 선택" };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var bytes = File.ReadAllBytes(dlg.FileName);
            var mime = Path.GetExtension(dlg.FileName).ToLowerInvariant() == ".png" ? "image/png" : "image/jpeg";
            CoverService.SetCover(first.Path, bytes, mime);
            first.CoverBytes = bytes;
            UpdateTaskPanel();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"커버 적용 중 오류: {ex.Message}", "TuneSync", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyCoverToSelection_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedItems();
        var source = selected.FirstOrDefault();
        if (source?.CoverBytes is null)
        {
            MessageBox.Show("먼저 기준이 될 커버가 있는 항목을 선택해 주세요.", "TuneSync");
            return;
        }
        var targets = selected.Where(r => r != source).ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show("같은 커버를 적용할 다른 항목도 함께 선택해 주세요.", "TuneSync");
            return;
        }
        if (MessageBox.Show($"선택한 {targets.Count}개 항목에 같은 커버를 적용할까요?", "TuneSync", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;

        foreach (var t in targets)
            CoverService.SetCover(t.Path, source.CoverBytes, "image/jpeg");
        foreach (var t in targets)
            t.CoverBytes = source.CoverBytes;
    }

    // ----- Rename tab -----

    private void LoadRulesIntoForm(RenameRuleSet rules)
    {
        SinglePatternBox.Text = rules.SingleTrackPattern;
        AlbumPatternBox.Text = rules.AlbumTrackPattern;
        ColonReplacementBox.Text = rules.ColonReplacement;
        OtherReplacementBox.Text = rules.OtherForbiddenReplacement;
    }

    private RenameRuleSet ReadRulesFromForm() => new()
    {
        SingleTrackPattern = SinglePatternBox.Text,
        AlbumTrackPattern = AlbumPatternBox.Text,
        ColonReplacement = ColonReplacementBox.Text,
        OtherForbiddenReplacement = OtherReplacementBox.Text,
    };

    private void SaveRenameRules_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.SaveRenameRules(ReadRulesFromForm());
        MessageBox.Show("규칙을 저장했습니다.", "TuneSync");
    }

    private void ResetRenameRules_Click(object sender, RoutedEventArgs e) => LoadRulesIntoForm(new RenameRuleSet());

    private void PreviewRename_Click(object sender, RoutedEventArgs e)
    {
        var targets = GetTaskTargets();
        if (targets.Count == 0)
        {
            MessageBox.Show("폴더를 선택해 주세요.", "TuneSync");
            return;
        }

        var rules = ReadRulesFromForm();
        _renamePreview.Clear();
        _renameApplyList = [];
        var plannedNewNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in targets)
        {
            var folder = Path.GetDirectoryName(item.Path)!;
            var newBase = RenameService.BuildFileName(item.Tags, rules, IsAlbumCheck.IsChecked == true);
            var newName = newBase + Path.GetExtension(item.Path);
            var oldName = Path.GetFileName(item.Path);
            var newPath = Path.Combine(folder, newName);

            string status;
            if (string.Equals(oldName, newName, StringComparison.Ordinal))
            {
                status = "변경없음";
            }
            else if (!plannedNewNames.Add(newPath.ToLowerInvariant()) ||
                     (File.Exists(newPath) && !string.Equals(newPath, item.Path, StringComparison.OrdinalIgnoreCase)))
            {
                status = "충돌(건너뜀)";
            }
            else
            {
                status = "변경";
                _renameApplyList.Add((item.Path, newPath));
            }

            _renamePreview.Add(new RenameRow(oldName, newName, status));
        }
    }

    private void ApplyRename_Click(object sender, RoutedEventArgs e)
    {
        if (_renameApplyList.Count == 0)
        {
            MessageBox.Show("먼저 미리보기를 실행해 주세요. (변경할 항목이 없습니다)", "TuneSync");
            return;
        }
        if (MessageBox.Show($"{_renameApplyList.Count}개 파일 이름을 변경할까요?", "TuneSync", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;

        var errors = new List<string>();
        foreach (var (oldPath, newPath) in _renameApplyList)
        {
            try
            {
                // Case-only renames silently no-op on some network shares; always go through a
                // temp name so the rename is never a no-op "same path, different case" operation.
                var temp = newPath + ".tunesync_tmp";
                File.Move(oldPath, temp);
                File.Move(temp, newPath);

                var oldLrc = Path.ChangeExtension(oldPath, ".lrc");
                if (File.Exists(oldLrc))
                {
                    var newLrc = Path.ChangeExtension(newPath, ".lrc");
                    var tempLrc = newLrc + ".tunesync_tmp";
                    File.Move(oldLrc, tempLrc);
                    File.Move(tempLrc, newLrc);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(oldPath)}: {ex.Message}");
            }
        }

        _renameApplyList = [];
        LoadCurrentFolder();
        _renamePreview.Clear();
        MessageBox.Show(errors.Count == 0 ? "완료했습니다." : $"일부 실패:\n{string.Join("\n", errors)}", "TuneSync");
    }

    // ----- Lyrics tab -----

    private async void SearchLyricsSelected_Click(object sender, RoutedEventArgs e) => await SearchLyrics(GetSelectedItems());

    private async void SearchLyricsAll_Click(object sender, RoutedEventArgs e) => await SearchLyrics(_files.ToList());

    private async Task SearchLyrics(List<LibraryFileItem> targets)
    {
        if (targets.Count == 0) return;
        _lyricsResults.Clear();
        foreach (var item in targets) _lyricsResults.Add(item);

        var done = 0;
        foreach (var item in targets)
        {
            LyricsStatusText.Text = $"검색 중... ({++done}/{targets.Count})";
            if (string.IsNullOrWhiteSpace(item.Tags.Artist) || string.IsNullOrWhiteSpace(item.Tags.Title))
            {
                item.FoundLyrics = null;
                item.LyricsSearchLabel = "아티스트/제목 없음";
                continue;
            }
            var result = await _lyricsService.FindAsync(item.Tags.Artist, item.Tags.Title);
            item.FoundLyrics = result;
            item.LyricsSearchLabel = result switch
            {
                null => "못 찾음",
                { Instrumental: true } => "연주곡(가사 없음)",
                { BestLyrics: null } => "못 찾음",
                { SyncedLyrics: not null } => "동기화 가사",
                _ => "일반 가사",
            };
        }
        LyricsStatusText.Text = $"검색 완료 ({targets.Count}건)";
    }

    private void ApplyLyricsSelected_Click(object sender, RoutedEventArgs e) => ApplyLyrics(GetSelectedItems());

    private void ApplyLyricsAllFound_Click(object sender, RoutedEventArgs e) =>
        ApplyLyrics(_files.Where(r => r.FoundLyrics?.BestLyrics is not null).ToList());

    private void ApplyLyrics(List<LibraryFileItem> targets)
    {
        targets = targets.Where(r => r.FoundLyrics?.BestLyrics is not null).ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show("적용할 가사가 없습니다. 먼저 검색해 주세요.", "TuneSync");
            return;
        }
        if (MessageBox.Show($"{targets.Count}개 파일에 가사를 적용할까요?", "TuneSync", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;

        var errors = new List<string>();
        foreach (var item in targets)
        {
            try
            {
                TagService.WriteLyrics(item.Path, item.FoundLyrics!.BestLyrics!);
                item.HasLyrics = true;
            }
            catch (Exception ex)
            {
                errors.Add($"{item.FileName}: {ex.Message}");
            }
        }
        LyricsStatusText.Text = errors.Count == 0 ? $"{targets.Count}건 적용 완료" : $"{errors.Count}건 실패";
        if (errors.Count > 0) MessageBox.Show(string.Join("\n", errors), "일부 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ----- Tag tab -----

    private void SaveTags_Click(object sender, RoutedEventArgs e)
    {
        TagEditGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var errors = new List<string>();
        var items = TagEditGrid.Items.Cast<LibraryFileItem>().ToList();
        foreach (var item in items)
        {
            try { TagService.Write(item.Tags); }
            catch (Exception ex) { errors.Add($"{item.FileName}: {ex.Message}"); }
        }
        TagStatusText.Text = errors.Count == 0 ? $"{items.Count}개 파일 저장 완료" : $"{items.Count - errors.Count}개 저장, {errors.Count}개 실패";
        FileListGrid.Items.Refresh();
        if (errors.Count > 0) MessageBox.Show(string.Join("\n", errors), "일부 저장 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private sealed record RenameRow(string OldName, string NewName, string Status);
}
