using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using TuneSync.App.ViewModels;

namespace TuneSync.App.Views;

/// <summary>The one-song tag editor (see <see cref="SongEditorViewModel"/>), in its own window.</summary>
public partial class SongEditorView : Page
{
    private readonly SongEditorViewModel _viewModel;
    private bool _closeConfirmed;

    private SongEditorView(SongEditorViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += (_, _) =>
        {
            if (Window.GetWindow(this) is not { } window || window.Tag is "hooked") return;
            window.Tag = "hooked";
            window.Closing += Window_Closing;
            window.Closed += (_, _) => _viewModel.Editor.Detach();
        };
    }

    /// <summary>Opens <paramref name="songs"/> (one, or several to step through) at
    /// <paramref name="index"/>, with an editor of its own: its edits and its save don't mix with
    /// 태그 관리's.</summary>
    public static void Open(Window? owner, IReadOnlyList<LibraryFileViewModel> songs, int index = 0)
    {
        if (songs.Count == 0) return;
        var services = ((App)Application.Current).Services;
        var editor = ActivatorUtilities.CreateInstance<TagEditorViewModel>(services);
        var page = new SongEditorView(new SongEditorViewModel(editor, songs, index));
        var window = new ToolWindow(page, "태그 편집", 1040, 720) { Owner = owner, MinWidth = 860, MinHeight = 600 };
        window.Show();
    }

    private TagEditorViewModel Editor => _viewModel.Editor;

    // ---- keys: Ctrl+S save, Alt+←/→ previous/next song, Esc close ----

    private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Editor.SaveCommand.Execute(null);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey is Key.Left or Key.Right)
        {
            var command = e.SystemKey == Key.Left ? _viewModel.PreviousCommand : _viewModel.NextCommand;
            if (command.CanExecute(null)) command.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Esc closes - after a field's own Esc (which reverts it) has had its turn.</summary>
    private void Page_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Window.GetWindow(this)?.Close();
        e.Handled = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Window.GetWindow(this)?.Close();

    /// <summary>Unsaved edits: ask before they're dropped.</summary>
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed || Editor.PendingFiles.Count == 0 || Editor.IsSaving) return;
        e.Cancel = true;
        var count = Editor.PendingFiles.Count;
        var discard = await Dialogs.ConfirmAsync("저장하지 않은 변경",
            count == 1 ? "저장하지 않은 변경이 있습니다. 버리고 닫을까요?" : $"{count}곡에 저장하지 않은 변경이 있습니다. 버리고 닫을까요?",
            "버리고 닫기");
        if (!discard) return;
        foreach (var file in Editor.PendingFiles.ToList()) file.ClearAllPending();
        _closeConfirmed = true;
        (sender as Window)?.Close();
    }

    // ---- cover ----

    /// <summary>The cover search shows its recommendation on the left and the other matches in
    /// 커버 후보.</summary>
    private void SearchCover_Click(object sender, RoutedEventArgs e)
    {
        Editor.Tab = EditorTab.Cover;
        Editor.SearchCoverCommand.Execute(null);
    }

    private void Cover_Click(object sender, MouseButtonEventArgs e)
    {
        if (Editor.CoverPreviewItems() is { Length: > 0 } items) CoverPreviewWindow.Open(Window.GetWindow(this), items);
    }

    private static string? DroppedImage(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files ? files.FirstOrDefault(TagEditorViewModel.IsImageFile) : null;

    private void CoverDrop_DragOver(object sender, DragEventArgs e)
    {
        var ok = DroppedImage(e) is not null;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        CoverDropHighlight.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void CoverDrop_DragLeave(object sender, DragEventArgs e) => CoverDropHighlight.Visibility = Visibility.Collapsed;

    private void CoverDrop_Drop(object sender, DragEventArgs e)
    {
        CoverDropHighlight.Visibility = Visibility.Collapsed;
        if (DroppedImage(e) is { } path) Editor.SetCoverFromFile(path);
        e.Handled = true;
    }

    private void CandidateScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) => CoverThumbnails.CheckAll(CandidateList);

    private void RevealFile_Click(object sender, RoutedEventArgs e) => PlayView.RevealInExplorer(_viewModel.Current.Path);
}
