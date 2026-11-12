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
    [NotifyPropertyChangedFor(nameof(Placeholder))]
    private bool _isMixed;

    public string Placeholder => IsMixed ? "여러 값" : "";

    /// <summary>The selection has an unsaved edit in this field.</summary>
    [ObservableProperty]
    private bool _isChanged;

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
