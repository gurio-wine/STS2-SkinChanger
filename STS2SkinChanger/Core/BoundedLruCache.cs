namespace STS2SkinChanger.Core;

internal sealed class BoundedLruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, CacheEntry> _entries;
    private readonly LinkedList<TKey> _recency = new();

    public BoundedLruCache(int capacity, IEqualityComparer<TKey>? comparer = null)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
        _entries = new Dictionary<TKey, CacheEntry>(comparer);
    }

    public int Count => _entries.Count;

    public IReadOnlyCollection<TKey> Keys => _entries.Keys.ToArray();

    public bool ContainsKey(TKey key) => _entries.ContainsKey(key);

    public bool TryGetValue(TKey key, out TValue value)
    {
        if (!_entries.TryGetValue(key, out var entry))
        {
            value = default!;
            return false;
        }

        Touch(entry);
        value = entry.Value;
        return true;
    }

    public bool Set(
        TKey key,
        TValue value,
        out KeyValuePair<TKey, TValue> evicted)
    {
        if (_entries.TryGetValue(key, out var existing))
        {
            existing.Value = value;
            Touch(existing);
            evicted = default;
            return false;
        }

        var node = _recency.AddFirst(key);
        _entries.Add(key, new CacheEntry(value, node));
        if (_entries.Count <= _capacity)
        {
            evicted = default;
            return false;
        }

        var oldestNode = _recency.Last!;
        var oldestKey = oldestNode.Value;
        var oldestEntry = _entries[oldestKey];
        _recency.RemoveLast();
        _entries.Remove(oldestKey);
        evicted = new KeyValuePair<TKey, TValue>(oldestKey, oldestEntry.Value);
        return true;
    }

    public bool Remove(TKey key)
    {
        if (!_entries.Remove(key, out var entry))
        {
            return false;
        }

        _recency.Remove(entry.Node);
        return true;
    }

    public void Clear()
    {
        _entries.Clear();
        _recency.Clear();
    }

    private void Touch(CacheEntry entry)
    {
        if (ReferenceEquals(_recency.First, entry.Node))
        {
            return;
        }

        _recency.Remove(entry.Node);
        _recency.AddFirst(entry.Node);
    }

    private sealed class CacheEntry(TValue value, LinkedListNode<TKey> node)
    {
        public TValue Value { get; set; } = value;
        public LinkedListNode<TKey> Node { get; } = node;
    }
}
