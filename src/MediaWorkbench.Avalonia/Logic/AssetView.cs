using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;

namespace MediaWorkbench.Avalonia;

/// <summary>
/// The filmstrip's list: the files that pass the filters, in the chosen order. Avalonia has no collection view like WPF's, so this
/// does the same job. A change is applied as the few inserts and removals it really is, worked out by walking the old and new
/// lists side by side, so the filmstrip keeps its scroll position and its selection; only a new sort order, or a change so large
/// that individual events would cost more than starting over, redraws the whole list.
/// </summary>
public sealed class AssetView : IReadOnlyList<AssetViewModel>, IList, INotifyCollectionChanged, INotifyPropertyChanged
{
    private const int ResetThreshold = 300;
    private readonly IReadOnlyList<AssetViewModel> source;
    private List<AssetViewModel> items = [];
    private IComparer<AssetViewModel>? sort;

    public AssetView(IReadOnlyList<AssetViewModel> source, Predicate<AssetViewModel> filter)
    {
        this.source = source;
        Filter = filter;
        if (source is INotifyCollectionChanged changes)
            changes.CollectionChanged += (_, _) => Refresh();
        Refresh();
    }

    public Predicate<AssetViewModel> Filter { get; }

    /// <summary>Setting a new order redraws the list in that order.</summary>
    public IComparer<AssetViewModel>? CustomSort
    {
        get => sort;
        set
        {
            sort = value;
            Refresh(reorder: true);
        }
    }

    public event NotifyCollectionChangedEventHandler? CollectionChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    public int Count => items.Count;
    public AssetViewModel this[int index] => items[index];
    public AssetViewModel GetItemAt(int index) => items[index];
    public int IndexOf(AssetViewModel item) => items.IndexOf(item);

    public void Refresh() => Refresh(reorder: false);

    private void Refresh(bool reorder)
    {
        IEnumerable<AssetViewModel> passing = source.Where(item => Filter(item));
        if (sort is not null) passing = passing.Order(sort);
        var next = passing.ToList();
        var old = items;
        if (old.Count == next.Count && old.SequenceEqual(next))
            return;
        if (reorder || Math.Abs(old.Count - next.Count) > ResetThreshold || !ApplyDifferences(old, next))
        {
            items = next;
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    /// <summary>
    /// Turns the old list into the new one with single inserts and removals, raising an event for each. Both lists are in the same
    /// order, so walking them together finds every difference. Returns false when the order itself changed (then nothing is
    /// raised and the caller starts over).
    /// </summary>
    private bool ApplyDifferences(List<AssetViewModel> old, List<AssetViewModel> next)
    {
        var keep = new HashSet<AssetViewModel>(next, ReferenceEqualityComparer.Instance);
        var had = new HashSet<AssetViewModel>(old, ReferenceEqualityComparer.Instance);
        // The items present in both lists must be in the same relative order, or this is a reorder.
        if (!old.Where(keep.Contains).SequenceEqual(next.Where(had.Contains)))
            return false;
        var changes = old.Count(item => !keep.Contains(item)) + next.Count(item => !had.Contains(item));
        if (changes > ResetThreshold)
            return false;
        var working = new List<AssetViewModel>(old);
        items = working;
        for (var index = working.Count - 1; index >= 0; index--)
        {
            if (keep.Contains(working[index])) continue;
            var removed = working[index];
            working.RemoveAt(index);
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, index));
        }
        for (var index = 0; index < next.Count; index++)
        {
            if (index < working.Count && ReferenceEquals(working[index], next[index])) continue;
            working.Insert(index, next[index]);
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, next[index], index));
        }
        return true;
    }

    public IEnumerator<AssetViewModel> GetEnumerator() => items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => items.GetEnumerator();

    // IList, read-only: lets list controls index into the view directly instead of copying it.
    bool IList.IsFixedSize => false;
    bool IList.IsReadOnly => true;
    bool ICollection.IsSynchronized => false;
    object ICollection.SyncRoot => this;
    object? IList.this[int index] { get => items[index]; set => throw new NotSupportedException(); }
    int IList.Add(object? value) => throw new NotSupportedException();
    void IList.Clear() => throw new NotSupportedException();
    bool IList.Contains(object? value) => value is AssetViewModel item && items.Contains(item);
    int IList.IndexOf(object? value) => value is AssetViewModel item ? items.IndexOf(item) : -1;
    void IList.Insert(int index, object? value) => throw new NotSupportedException();
    void IList.Remove(object? value) => throw new NotSupportedException();
    void IList.RemoveAt(int index) => throw new NotSupportedException();
    void ICollection.CopyTo(Array array, int index) => ((ICollection)items).CopyTo(array, index);
}
