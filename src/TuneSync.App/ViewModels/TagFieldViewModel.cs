using System.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneSync.Core.Models;

namespace TuneSync.App.ViewModels;

/// <summary>One field of the tag editor (제목, 아티스트, …) for the current selection: what it shows,
/// whether it's mixed across songs, whether it was edited, and what it was before.</summary>
public sealed partial class TagFieldViewModel : ObservableObject
{
    private readonly TagEditorViewModel _editor;
    private bool _loading;

    public TagFieldViewModel(TagEditorViewModel editor, TagField field, bool usesSuggestions = false)
    {
        _editor = editor;
        Field = field;
        UsesSuggestions = usesSuggestions;
    }

    public TagField Field { get; }
    public string Label => TagFields.Label(Field);
    public bool UsesSuggestions { get; }

    /// <summary>Values already used in the library, offered while typing (keeps spellings consistent).</summary>
    [ObservableProperty]
    private IList? _suggestions;

    [ObservableProperty]
    private string _text = "";

    /// <summary>The selected songs have different values; the box is empty and keeps each one
    /// unless something is typed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Placeholder), nameof(BatchStatus))]
    private bool _isMixed;

    public string Placeholder => IsMixed ? "여러 값" : "";

    /// <summary>The selection has an unsaved edit in this field.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsApplied), nameof(BatchStatus))]
    private bool _isChanged;

    // ---- 일괄 편집 (several songs): each field says whether it changes them all ----

    /// <summary>How many songs are selected (several: the field shows its "적용" check and status).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BatchStatus), nameof(IsBatch))]
    private int _selectionCount;

    /// <summary>일괄 편집: several songs at once.</summary>
    public bool IsBatch => SelectionCount > 1;

    /// <summary>How many of the selected songs this field changes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BatchStatus))]
    private int _changedCount;

    /// <summary>The "적용" check: on when the field changes the songs. Ticking it applies what the
    /// box shows to every selected song; clearing it leaves each song's own value.</summary>
    public bool IsApplied
    {
        get => IsChanged;
        set
        {
            if (value != IsChanged) _editor.SetFieldApplied(this, value);
            OnPropertyChanged(); // snaps back when nothing could be applied
        }
    }

    /// <summary>"14곡 모두 바뀜" / "14곡 중 3곡 바뀜" / "곡마다 다름 · 그대로 둠" / "14곡 모두 같음".</summary>
    public string BatchStatus =>
        ChangedCount > 0 && ChangedCount >= SelectionCount ? $"{SelectionCount}곡 모두 바뀜"
        : ChangedCount > 0 ? $"{SelectionCount}곡 중 {ChangedCount}곡 바뀜"
        : IsMixed ? "곡마다 다름 · 그대로 둠"
        : $"{SelectionCount}곡 모두 같음";

    /// <summary>Asks the view to put the caret in this field (ticking a mixed, empty field: there
    /// is nothing to apply until a value is typed).</summary>
    public event Action? FocusRequested;

    public void RequestFocus() => FocusRequested?.Invoke();

    /// <summary>What it was before the edit ("이전" line under a changed field).</summary>
    [ObservableProperty]
    private string _previousText = "";

    /// <summary>Several songs: how the current values are spread ("방탄소년단 10곡 · BTS 2곡").</summary>
    [ObservableProperty]
    private string _distribution = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasError => !string.IsNullOrEmpty(Error);

    partial void OnTextChanged(string value)
    {
        if (!_loading) _editor.OnFieldEdited(this);
    }

    /// <summary>Shows a value without treating it as the user's edit.</summary>
    public void Load(string text, bool isMixed)
    {
        _loading = true;
        try
        {
            Text = text;
            IsMixed = isMixed;
            Error = null;
        }
        finally
        {
            _loading = false;
        }
    }

    [RelayCommand]
    private void Revert() => _editor.RevertField(this);
}
