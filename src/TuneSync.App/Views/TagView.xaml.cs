using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.App.Views;

public partial class TagView : UserControl
{
    private readonly ObservableCollection<TrackTags> _rows = [];

    public TagView()
    {
        InitializeComponent();
        TagGrid.ItemsSource = _rows;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "태그를 확인/수정할 폴더 선택" };
        if (dlg.ShowDialog() == true)
        {
            FolderBox.Text = dlg.FolderName;
            LoadFolder();
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadFolder();

    private void LoadFolder()
    {
        TagGrid.CommitEdit(DataGridEditingUnit.Row, true);
        _rows.Clear();
        if (string.IsNullOrWhiteSpace(FolderBox.Text) || !Directory.Exists(FolderBox.Text)) return;

        foreach (var path in Directory.EnumerateFiles(FolderBox.Text).Where(TagService.IsSupported))
        {
            try { _rows.Add(TagService.Read(path)); }
            catch { /* unreadable tag header - skip */ }
        }
        StatusText.Text = $"{_rows.Count}개 파일 불러옴";
    }

    private void SaveAll_Click(object sender, RoutedEventArgs e)
    {
        TagGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var errors = new List<string>();
        foreach (var row in _rows)
        {
            try { TagService.Write(row); }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(row.Path)}: {ex.Message}"); }
        }
        StatusText.Text = errors.Count == 0
            ? $"{_rows.Count}개 파일 저장 완료"
            : $"{_rows.Count - errors.Count}개 저장, {errors.Count}개 실패";
        if (errors.Count > 0)
            MessageBox.Show(string.Join("\n", errors), "일부 저장 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
