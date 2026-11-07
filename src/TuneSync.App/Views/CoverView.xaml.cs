using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using TuneSync.Core.Services;

namespace TuneSync.App.Views;

public partial class CoverView : Page
{
    private readonly ObservableCollection<TrackRow> _rows = [];

    public CoverView()
    {
        InitializeComponent();
        TrackGrid.ItemsSource = _rows;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "커버를 확인할 폴더 선택" };
        if (dlg.ShowDialog() == true)
        {
            FolderBox.Text = dlg.FolderName;
            LoadFolder();
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadFolder();

    private void LoadFolder()
    {
        _rows.Clear();
        CoverImage.Source = null;
        if (string.IsNullOrWhiteSpace(FolderBox.Text) || !Directory.Exists(FolderBox.Text)) return;

        foreach (var path in Directory.EnumerateFiles(FolderBox.Text).Where(TagService.IsSupported))
        {
            try
            {
                var tags = TagService.Read(path);
                var cover = CoverService.GetCover(path);
                _rows.Add(new TrackRow(path, tags.Album, cover));
            }
            catch
            {
                // unreadable file (corrupt tag header etc.) - skip rather than crash the whole scan
            }
        }
    }

    private void TrackGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TrackGrid.SelectedItem is not TrackRow row)
        {
            CoverImage.Source = null;
            SelectionInfo.Text = "";
            return;
        }
        CoverImage.Source = ToBitmapImage(row.CoverBytes);
        var countSelected = TrackGrid.SelectedItems.Count;
        SelectionInfo.Text = countSelected > 1
            ? $"{countSelected}개 선택됨 (미리보기는 첫 항목 기준)"
            : row.FileName;
    }

    private void ReplaceFromFile_Click(object sender, RoutedEventArgs e)
    {
        if (TrackGrid.SelectedItem is not TrackRow row) return;

        var dlg = new OpenFileDialog { Filter = "이미지 파일|*.jpg;*.jpeg;*.png", Title = "새 커버 이미지 선택" };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var bytes = File.ReadAllBytes(dlg.FileName);
            var mime = Path.GetExtension(dlg.FileName).ToLowerInvariant() == ".png" ? "image/png" : "image/jpeg";
            CoverService.SetCover(row.Path, bytes, mime);
            row.CoverBytes = bytes;
            CoverImage.Source = ToBitmapImage(bytes);
            TrackGrid.Items.Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"커버 적용 중 오류: {ex.Message}", "TuneSync", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyToAll_Click(object sender, RoutedEventArgs e)
    {
        if (TrackGrid.SelectedItem is not TrackRow source || source.CoverBytes is null)
        {
            MessageBox.Show("먼저 기준이 될 커버가 있는 항목을 선택해 주세요.", "TuneSync");
            return;
        }
        var targets = TrackGrid.SelectedItems.Cast<TrackRow>().Where(r => r != source).ToList();
        if (targets.Count == 0) return;
        if (MessageBox.Show($"선택한 {targets.Count}개 항목에 같은 커버를 적용할까요? (휴대폰 동기화 폴더 등 같은 곡의 다른 사본에도 함께 적용하세요)",
                "TuneSync", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;

        var mime = "image/jpeg";
        foreach (var t in targets)
        {
            CoverService.SetCover(t.Path, source.CoverBytes, mime);
            t.CoverBytes = source.CoverBytes;
        }
        TrackGrid.Items.Refresh();
    }

    private static BitmapImage? ToBitmapImage(byte[]? bytes)
    {
        if (bytes is null) return null;
        var bmp = new BitmapImage();
        using var ms = new MemoryStream(bytes);
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private sealed class TrackRow(string path, string album, byte[]? coverBytes)
    {
        public string Path { get; } = path;
        public string FileName { get; } = System.IO.Path.GetFileName(path);
        public string Album { get; } = album;
        public byte[]? CoverBytes { get; set; } = coverBytes;
        public string HasCoverLabel => CoverBytes is null ? "없음" : "있음";
    }
}
