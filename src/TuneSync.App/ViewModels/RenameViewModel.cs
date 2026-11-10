using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
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
    private string _statusText = "폴더를 선택한 뒤 미리보기를 실행해 주세요.";

    private List<RenamePreviewItem>? _builtPreview;

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog { Title = "이름을 변경할 폴더 선택" };
        if (dialog.ShowDialog() == true)
        {
            FolderPath = dialog.FolderName;
            InvalidatePreview();
        }
    }

    [RelayCommand]
    private void BuildPreview()
    {
        if (string.IsNullOrWhiteSpace(FolderPath))
        {
            StatusText = "폴더를 먼저 선택해 주세요.";
            return;
        }

        var option = IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        List<string> files;
        try
        {
            files = [.. Directory.EnumerateFiles(FolderPath, "*.*", option).Where(TagService.IsSupported)];
        }
        catch (Exception ex)
        {
            StatusText = $"폴더를 읽는 중 오류: {ex.Message}";
            return;
        }

        var rules = new RenameRuleSet { SingleTrackPattern = SingleTrackPattern, AlbumTrackPattern = AlbumTrackPattern };
        _builtPreview = RenameService.BuildPreview(files, rules, IsAlbumMode);

        RefreshPreviewList();

        var changedCount = _builtPreview.Count(i => i.WillChange);
        IsPreviewReady = changedCount > 0;
        StatusText = _builtPreview.Count == 0
            ? "대상 음원 파일이 없습니다."
            : $"{_builtPreview.Count}개 중 {changedCount}개 파일의 이름이 변경됩니다.";
    }

    [RelayCommand]
    private void Execute()
    {
        if (_builtPreview is not { Count: > 0 }) return;

        var errors = RenameService.Execute(_builtPreview);
        RefreshPreviewList();

        var changed = _builtPreview.Count(i => i.Status == "변경 완료");
        StatusText = errors.Count == 0
            ? $"{changed}개 파일 이름을 변경했습니다."
            : $"{changed}개 변경, {errors.Count}개 실패: {string.Join("; ", errors)}";

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
        _builtPreview = null;
        Preview.Clear();
        IsPreviewReady = false;
    }
}
