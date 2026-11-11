using CommunityToolkit.Mvvm.ComponentModel;

namespace TuneSync.App.ViewModels;

/// <summary>One line of synced lyrics, with the highlight state the lyrics list binds to.</summary>
public sealed partial class LyricsLineViewModel(TimeSpan time, string text) : ObservableObject
{
    public TimeSpan Time { get; } = time;
    public string Text { get; } = text;

    [ObservableProperty]
    private bool _isCurrent;
}
