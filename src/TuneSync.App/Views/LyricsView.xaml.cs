using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.App.Views;

public partial class LyricsView : Page
{
    private readonly ObservableCollection<LyricsRow> _rows = [];
    private readonly LyricsService _lyricsService = new();

    public LyricsView()
    {
        InitializeComponent();
        TrackGrid.ItemsSource = _rows;
        Unloaded += (_, _) => _lyricsService.Dispose();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "가사를 적용할 폴더 선택" };
        if (dlg.ShowDialog() == true)
        {
            FolderBox.Text = dlg.FolderName;
            LoadFolder();
        }
    }

    private void LoadFolder()
    {
        _rows.Clear();
        if (string.IsNullOrWhiteSpace(FolderBox.Text) || !Directory.Exists(FolderBox.Text)) return;

        foreach (var path in Directory.EnumerateFiles(FolderBox.Text).Where(TagService.IsSupported))
        {
            try
            {
                var tags = TagService.Read(path);
                var existing = TagService.ReadLyrics(path);
                _rows.Add(new LyricsRow(path, tags.Artist, tags.Title, !string.IsNullOrWhiteSpace(existing)));
            }
            catch
            {
                // unreadable file - skip
            }
        }
    }

    private async void SearchSelected_Click(object sender, RoutedEventArgs e)
    {
        var targets = TrackGrid.SelectedItems.Cast<LyricsRow>().ToList();
        await SearchRows(targets);
    }

    private async void SearchAll_Click(object sender, RoutedEventArgs e) => await SearchRows(_rows.ToList());

    private async Task SearchRows(List<LyricsRow> targets)
    {
        if (targets.Count == 0) return;
        var done = 0;
        foreach (var row in targets)
        {
            StatusText.Text = $"검색 중... ({++done}/{targets.Count})";
            if (string.IsNullOrWhiteSpace(row.Artist) || string.IsNullOrWhiteSpace(row.Title))
            {
                row.Found = null;
                row.FoundLabel = "아티스트/제목 없음";
                continue;
            }
            var result = await _lyricsService.FindAsync(row.Artist, row.Title);
            row.Found = result;
            row.FoundLabel = result switch
            {
                null => "못 찾음",
                { Instrumental: true } => "연주곡(가사 없음)",
                { BestLyrics: null } => "못 찾음",
                { SyncedLyrics: not null } => "동기화 가사",
                _ => "일반 가사",
            };
        }
        TrackGrid.Items.Refresh();
        StatusText.Text = $"검색 완료 ({targets.Count}건)";
    }

    private void ApplySelected_Click(object sender, RoutedEventArgs e) =>
        ApplyRows(TrackGrid.SelectedItems.Cast<LyricsRow>().ToList());

    private void ApplyAllFound_Click(object sender, RoutedEventArgs e) =>
        ApplyRows(_rows.Where(r => r.Found?.BestLyrics is not null).ToList());

    private void ApplyRows(List<LyricsRow> targets)
    {
        targets = targets.Where(r => r.Found?.BestLyrics is not null).ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show("적용할 가사가 없습니다. 먼저 검색해 주세요.", "TuneSync");
            return;
        }
        if (MessageBox.Show($"{targets.Count}개 파일에 가사를 적용할까요?", "TuneSync", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;

        var errors = new List<string>();
        foreach (var row in targets)
        {
            try
            {
                TagService.WriteLyrics(row.Path, row.Found!.BestLyrics!);
                row.HasExistingLabel = "있음";
            }
            catch (Exception ex)
            {
                errors.Add($"{row.FileName}: {ex.Message}");
            }
        }
        TrackGrid.Items.Refresh();
        StatusText.Text = errors.Count == 0 ? $"{targets.Count}건 적용 완료" : $"{errors.Count}건 실패";
    }

    private sealed class LyricsRow(string path, string artist, string title, bool hasExisting)
    {
        public string Path { get; } = path;
        public string FileName { get; } = System.IO.Path.GetFileName(path);
        public string Artist { get; } = artist;
        public string Title { get; } = title;
        public string HasExistingLabel { get; set; } = hasExisting ? "있음" : "없음";
        public LyricsResult? Found { get; set; }
        public string FoundLabel { get; set; } = "미검색";
    }
}
