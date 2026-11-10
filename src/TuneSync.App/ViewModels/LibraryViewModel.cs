using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TuneSync.Core.Services;

namespace TuneSync.App.ViewModels;

public sealed partial class LibraryViewModel : ObservableObject
{
    public ObservableCollection<LibraryFileViewModel> Files { get; } = [];
    public ICollectionView FilesView { get; }
    public FilterPanelViewModel Filter { get; } = new();

    [ObservableProperty]
    private string? _currentFolder;

    [ObservableProperty]
    private bool _includeSubfolders;

    [ObservableProperty]
    private LibraryViewMode _viewMode = LibraryViewMode.List;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _statusText = "폴더를 선택해 주세요.";

    private CancellationTokenSource? _scanCts;

    public LibraryViewModel()
    {
        FilesView = CollectionViewSource.GetDefaultView(Files);
        FilesView.Filter = o => o is LibraryFileViewModel vm && Filter.Matches(vm);
        Filter.FilterChanged += (_, _) => FilesView.Refresh();
    }

    [RelayCommand]
    private async Task BrowseFolderAsync()
    {
        var dialog = new OpenFolderDialog { Title = "음악 폴더 선택" };
        if (dialog.ShowDialog() != true) return;

        CurrentFolder = dialog.FolderName;
        await RescanAsync();
    }

    [RelayCommand]
    private async Task RescanAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentFolder)) return;

        _scanCts?.Cancel();
        var cts = new CancellationTokenSource();
        _scanCts = cts;

        Files.Clear();
        IsScanning = true;
        StatusText = "스캔 중...";
        var count = 0;

        try
        {
            await foreach (var file in MediaLibraryScanner.ScanAsync(CurrentFolder, IncludeSubfolders, cts.Token))
            {
                if (cts.IsCancellationRequested) break;
                Files.Add(new LibraryFileViewModel(file));
                count++;
                StatusText = $"스캔 중... ({count}개)";
            }

            if (!cts.IsCancellationRequested)
                StatusText = count == 0 ? "음원 파일을 찾지 못했습니다." : $"{count}개 파일";
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

    partial void OnIncludeSubfoldersChanged(bool value)
    {
        if (!string.IsNullOrWhiteSpace(CurrentFolder)) _ = RescanAsync();
    }
}
