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
}
