using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace TuneSync.App;

/// <summary>An ObservableCollection that can take many items with one change notification.
/// WPF's collection views can't handle a multi-item Add, and one Add per item made the sorted,
/// grouped library views re-sort and redraw for every song (seconds of freezing for a big
/// library), so <see cref="AddRange"/> raises a single Reset instead.</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void AddRange(IReadOnlyCollection<T> items)
    {
        if (items.Count == 0) return;
        CheckReentrancy();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
