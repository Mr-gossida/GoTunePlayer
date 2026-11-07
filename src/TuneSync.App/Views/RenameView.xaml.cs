using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.App.Views;

public partial class RenameView : Page
{
    private readonly ObservableCollection<RenameRow> _rows = [];
    private List<(string OldPath, string NewPath)> _applyList = [];

    public RenameView()
    {
        InitializeComponent();
        PreviewGrid.ItemsSource = _rows;
        LoadRulesIntoForm(AppSettings.LoadRenameRules());
    }

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

    private void SaveRules_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.SaveRenameRules(ReadRulesFromForm());
        MessageBox.Show("규칙을 저장했습니다.", "TuneSync");
    }

    private void ResetRules_Click(object sender, RoutedEventArgs e) => LoadRulesIntoForm(new RenameRuleSet());

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "이름을 정리할 폴더 선택" };
        if (dlg.ShowDialog() == true) FolderBox.Text = dlg.FolderName;
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(FolderBox.Text) || !Directory.Exists(FolderBox.Text))
        {
            MessageBox.Show("폴더를 선택해 주세요.", "TuneSync");
            return;
        }

        var rules = ReadRulesFromForm();
        _rows.Clear();
        _applyList = [];
        var plannedNewNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(FolderBox.Text).Where(TagService.IsSupported))
        {
            TrackTags tags;
            try { tags = TagService.Read(path); }
            catch { continue; }

            var newBase = RenameService.BuildFileName(tags, rules, IsAlbumCheck.IsChecked == true);
            var newName = newBase + Path.GetExtension(path);
            var oldName = Path.GetFileName(path);
            var newPath = Path.Combine(FolderBox.Text, newName);

            string status;
            if (string.Equals(oldName, newName, StringComparison.Ordinal))
            {
                status = "변경없음";
            }
            else if (!plannedNewNames.Add(newName.ToLowerInvariant()) ||
                     (System.IO.File.Exists(newPath) && !string.Equals(newPath, path, StringComparison.OrdinalIgnoreCase)))
            {
                status = "충돌(건너뜀)";
            }
            else
            {
                status = "변경";
                _applyList.Add((path, newPath));
            }

            _rows.Add(new RenameRow(oldName, newName, status));
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_applyList.Count == 0)
        {
            MessageBox.Show("먼저 미리보기를 실행해 주세요. (변경할 항목이 없습니다)", "TuneSync");
            return;
        }
        if (MessageBox.Show($"{_applyList.Count}개 파일 이름을 변경할까요?", "TuneSync", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;

        var errors = new List<string>();
        foreach (var (oldPath, newPath) in _applyList)
        {
            try
            {
                // Case-only renames silently no-op on some network shares; always go through a
                // temp name so the rename is never a no-op "same path, different case" operation.
                var temp = newPath + ".tunesync_tmp";
                System.IO.File.Move(oldPath, temp);
                System.IO.File.Move(temp, newPath);

                var oldLrc = Path.ChangeExtension(oldPath, ".lrc");
                if (System.IO.File.Exists(oldLrc))
                {
                    var newLrc = Path.ChangeExtension(newPath, ".lrc");
                    var tempLrc = newLrc + ".tunesync_tmp";
                    System.IO.File.Move(oldLrc, tempLrc);
                    System.IO.File.Move(tempLrc, newLrc);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(oldPath)}: {ex.Message}");
            }
        }

        _applyList = [];
        Preview_Click(sender, e);
        MessageBox.Show(errors.Count == 0 ? "완료했습니다." : $"일부 실패:\n{string.Join("\n", errors)}", "TuneSync");
    }

    private sealed record RenameRow(string OldName, string NewName, string Status);
}
