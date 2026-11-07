using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.App.Views;

public partial class MergeView : Page
{
    private readonly ObservableCollection<MergePlanRow> _rows = [];
    private List<MergePlanItem> _plan = [];
    private RenameRuleSet _rules = AppSettings.LoadRenameRules();

    public MergeView()
    {
        InitializeComponent();
        PlanGrid.ItemsSource = _rows;
    }

    private void BrowseSource_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "합칠 원본 폴더 선택" };
        if (dlg.ShowDialog() == true) SourceFolderBox.Text = dlg.FolderName;
    }

    private void BrowseDest_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "대상(목적지) 폴더 선택" };
        if (dlg.ShowDialog() == true) DestFolderBox.Text = dlg.FolderName;
    }

    private void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateFolders()) return;
        try
        {
            _rules = AppSettings.LoadRenameRules();
            _plan = MergeService.BuildPlan(SourceFolderBox.Text, DestFolderBox.Text, _rules);
            _rows.Clear();
            foreach (var item in _plan) _rows.Add(new MergePlanRow(item));
            StatusText.Text = $"{_plan.Count}개 항목 중 충돌 {_plan.Count(p => p.Action != MergeAction.Copy)}건";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"미리보기 중 오류: {ex.Message}", "TuneSync", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Execute_Click(object sender, RoutedEventArgs e)
    {
        if (_plan.Count == 0)
        {
            MessageBox.Show("먼저 미리보기를 실행해 주세요.", "TuneSync");
            return;
        }
        if (MessageBox.Show($"{_plan.Count}개 항목을 적용하시겠습니까?", "TuneSync", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;

        try
        {
            var quarantine = Path.Combine(Path.GetDirectoryName(DestFolderBox.Text.TrimEnd('\\'))!,
                "_quarantine_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            MergeService.Execute(_plan, DestFolderBox.Text, quarantine);
            StatusText.Text = $"완료. 밀려난 파일은 다음 폴더로 이동됨: {quarantine}";
            _plan = [];
            _rows.Clear();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"적용 중 오류: {ex.Message}", "TuneSync", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool ValidateFolders()
    {
        if (string.IsNullOrWhiteSpace(SourceFolderBox.Text) || !Directory.Exists(SourceFolderBox.Text))
        {
            MessageBox.Show("원본 폴더를 선택해 주세요.", "TuneSync");
            return false;
        }
        if (string.IsNullOrWhiteSpace(DestFolderBox.Text) || !Directory.Exists(DestFolderBox.Text))
        {
            MessageBox.Show("대상 폴더를 선택해 주세요.", "TuneSync");
            return false;
        }
        return true;
    }

    private sealed class MergePlanRow(MergePlanItem item)
    {
        public string ActionLabel => item.Action switch
        {
            MergeAction.Copy => "이동",
            MergeAction.KeepExisting => "중복(기존 유지)",
            MergeAction.ReplaceExisting => "중복(신규 유지)",
            _ => item.Action.ToString(),
        };
        public string SourceName => Path.GetFileName(item.SourcePath);
        public string DestFileName => item.DestFileName;
        public string ConflictName => item.ConflictWithPath is null ? "" : Path.GetFileName(item.ConflictWithPath);
        public string SourceSizeKb => $"{item.SourceSize / 1024:N0} KB";
        public string ConflictSizeKb => item.ConflictSize is null ? "" : $"{item.ConflictSize / 1024:N0} KB";
    }
}
