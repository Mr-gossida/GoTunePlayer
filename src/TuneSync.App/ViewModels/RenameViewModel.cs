using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.App.ViewModels;

public sealed partial class RenameViewModel : ObservableObject
{
    public ObservableCollection<RenamePreviewItemViewModel> Preview { get; } = [];

    [ObservableProperty]
    private string? _folderPath;

    [ObservableProperty]
    private bool _includeSubfolders;

    [ObservableProperty]
    private bool _isAlbumMode;

    [ObservableProperty]
    private string _singleTrackPattern = "{Artist} - {Title}";

    [ObservableProperty]
    private string _albumTrackPattern = "{Track} {Title}";

    [ObservableProperty]
    private bool _isPreviewReady;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "폴더를 선택한 뒤 미리보기를 실행해 주세요.";

    private List<RenamePreviewItem>? _builtPreview;
    private readonly AppSettings _settings;
    private readonly LibraryViewModel _library;
    private readonly PlaybackViewModel _playback;

    public RenameViewModel(AppSettings settings, LibraryViewModel library, PlaybackViewModel playback)
    {
        _settings = settings;
        _library = library;
        _playback = playback;

        _folderPath = settings.LastRenameFolder;
        _includeSubfolders = settings.RenameIncludeSubfolders;
        _isAlbumMode = settings.RenameIsAlbumMode;
        _singleTrackPattern = settings.RenameSingleTrackPattern;
        _albumTrackPattern = settings.RenameAlbumTrackPattern;
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var folder = FileSystemPicker.PickFolder("이름을 변경할 폴더 선택 (폴더로 이동한 뒤 '선택'을 누르세요)", FolderPath);
        if (folder is not null)
        {
            FolderPath = folder;
            _settings.LastRenameFolder = FolderPath;
            AppSettingsStore.Save(_settings);
            InvalidatePreview();
        }
    }

    // Any change to the inputs makes an existing preview stale - "적용" must never rename using
    // names computed from settings the user has since changed.
    partial void OnIncludeSubfoldersChanged(bool value)
    {
        _settings.RenameIncludeSubfolders = value;
        AppSettingsStore.Save(_settings);
        InvalidatePreview();
    }

    partial void OnIsAlbumModeChanged(bool value)
    {
        _settings.RenameIsAlbumMode = value;
        AppSettingsStore.Save(_settings);
        InvalidatePreview();
    }

    partial void OnSingleTrackPatternChanged(string value) => InvalidatePreview();

    partial void OnAlbumTrackPatternChanged(string value) => InvalidatePreview();

    [RelayCommand]
    private async Task BuildPreviewAsync()
    {
        if (string.IsNullOrWhiteSpace(FolderPath))
        {
            StatusText = "폴더를 먼저 선택해 주세요.";
            return;
        }

        if (!Directory.Exists(FolderPath))
        {
            StatusText = "폴더를 찾을 수 없습니다. 폴더를 다시 선택해 주세요.";
            return;
        }

        var activePattern = IsAlbumMode ? AlbumTrackPattern : SingleTrackPattern;
        if (string.IsNullOrWhiteSpace(activePattern) || !activePattern.Contains('{'))
        {
            StatusText = "패턴에 {Artist}, {Title} 같은 토큰을 하나 이상 넣어 주세요.";
            return;
        }

        var option = IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var folderPath = FolderPath;
        var rules = new RenameRuleSet { SingleTrackPattern = SingleTrackPattern, AlbumTrackPattern = AlbumTrackPattern };
        var isAlbumMode = IsAlbumMode;

        _settings.RenameSingleTrackPattern = SingleTrackPattern;
        _settings.RenameAlbumTrackPattern = AlbumTrackPattern;
        AppSettingsStore.Save(_settings);

        IsBusy = true;
        StatusText = "폴더를 분석하는 중...";
        try
        {
            // Reads every file's tags - can take a while for large folders, so it must never run
            // on the UI thread (it did; the app froze).
            _builtPreview = await Task.Run(() =>
            {
                var files = Directory.EnumerateFiles(folderPath, "*.*", option).Where(TagService.IsSupported);
                return RenameService.BuildPreview(files, rules, isAlbumMode);
            });
        }
        catch (Exception ex)
        {
            StatusText = $"폴더를 읽는 중 오류: {ex.Message}";
            Toast.Error("미리보기 생성 실패", ex.Message);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        RefreshPreviewList();

        var changedCount = _builtPreview.Count(i => i.WillChange);
        IsPreviewReady = changedCount > 0;
        StatusText = _builtPreview.Count == 0
            ? "대상 음원 파일이 없습니다."
            : $"{_builtPreview.Count}개 중 {changedCount}개 파일의 이름이 변경됩니다.";
    }

    [RelayCommand]
    private async Task ExecuteAsync()
    {
        if (_builtPreview is not { Count: > 0 } preview) return;

        var toChange = preview.Count(i => i.WillChange);
        var confirmed = await Dialogs.ConfirmAsync("이름 변경 적용",
            $"{toChange}개 파일의 이름을 변경합니다.\n\n계속할까요?", "적용");
        if (!confirmed) return;

        var folderPath = FolderPath;
        IsBusy = true;
        StatusText = "파일 이름을 변경하는 중...";
        List<string> errors;
        // A playing file can't be renamed while the player holds it; release it, then resume
        // from its new name (or the old one, if its rename failed).
        var resume = await _playback.ReleaseIfPlayingAsync(preview.Where(i => i.WillChange).Select(i => i.OldPath));
        try
        {
            errors = await Task.Run(() => RenameService.Execute(preview));
        }
        catch (Exception ex)
        {
            resume?.Invoke(null);
            StatusText = $"이름 변경 중 오류: {ex.Message}";
            Toast.Error("이름 변경 실패", ex.Message);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        if (resume is not null)
        {
            var playing = preview.FirstOrDefault(i => string.Equals(i.OldPath, _playback.CurrentFile?.Path, StringComparison.OrdinalIgnoreCase));
            resume(playing is not null && File.Exists(playing.NewPath) ? playing.NewPath : null);
        }

        _library.RescanIfAffected(folderPath);

        RefreshPreviewList();

        var changed = preview.Count(i => i.Status == "변경 완료");
        StatusText = errors.Count == 0
            ? $"{changed}개 파일 이름을 변경했습니다."
            : $"{changed}개 변경, {errors.Count}개 실패: {string.Join("; ", errors)}";
        if (errors.Count == 0)
            Toast.Success("이름 변경 완료", $"{changed}개 파일 이름을 변경했습니다.");
        else
            Toast.Warning("일부 파일 이름을 바꾸지 못했습니다", $"{changed}개 변경, {errors.Count}개 실패. 표에서 상태를 확인해 주세요.");

        IsPreviewReady = false;
        _builtPreview = null;
    }

    private void RefreshPreviewList()
    {
        Preview.Clear();
        if (_builtPreview is null) return;
        foreach (var item in _builtPreview)
            Preview.Add(new RenamePreviewItemViewModel(item));
    }

    private void InvalidatePreview()
    {
        if (_builtPreview is null && Preview.Count == 0) return;
        _builtPreview = null;
        Preview.Clear();
        IsPreviewReady = false;
        StatusText = "설정이 바뀌었습니다. 미리보기를 다시 실행해 주세요.";
    }
}
