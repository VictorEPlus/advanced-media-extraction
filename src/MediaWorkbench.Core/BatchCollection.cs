using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace MediaWorkbench.Core;

public sealed class BatchCollection<T> : ObservableCollection<T>
{
    public void AddRange(IEnumerable<T> values)
    {
        var items = values.ToArray();
        if (items.Length == 0) return;
        CheckReentrancy();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>Removes every item that matches, with a single change notification. Returns how many went.</summary>
    public int RemoveWhere(Func<T, bool> match)
    {
        CheckReentrancy();
        var removed = 0;
        for (var index = Items.Count - 1; index >= 0; index--)
        {
            if (!match(Items[index])) continue;
            Items.RemoveAt(index);
            removed++;
        }
        if (removed == 0) return 0;
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        return removed;
    }
}
