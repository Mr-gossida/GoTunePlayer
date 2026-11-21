using CommunityToolkit.Mvvm.ComponentModel;

namespace TuneSync.App.ViewModels;

/// <summary>One look in a 무작위 setting's pool (설정 › 화면 효과): checked, 무작위 may pick it.</summary>
public sealed class RandomPoolItem : ObservableObject
{
    private readonly Action _changed;
    private bool _isChecked;

    public RandomPoolItem(object value, string label, bool isChecked, Action changed)
    {
        Value = value;
        Label = label;
        _isChecked = isChecked;
        _changed = changed;
    }

    public object Value { get; }
    public string Label { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value)) _changed();
        }
    }
}
