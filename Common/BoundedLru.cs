namespace Common;

/// <summary>A thread-safe map that forgets its least recently used entry once it holds <c>capacity</c>.</summary>
public sealed class BoundedLru<TKey, TValue>(int capacity) where TKey : notnull
{
    private readonly Lock _lock = new();
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _entries = [];
    private readonly LinkedList<(TKey Key, TValue Value)> _order = [];

    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var node))
            {
                value = default!;
                return false;
            }
            _order.Remove(node);
            _order.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
    }

    public void Set(TKey key, TValue value) => AddOrUpdate(key, _ => value);

    /// <summary>Stores the value, and says whether it differs from the one held (true when absent), atomically.</summary>
    public bool SetIfChanged(TKey key, TValue value)
    {
        var changed = false;
        AddOrUpdate(key, current =>
        {
            changed = !EqualityComparer<TValue?>.Default.Equals(current, value);
            return value;
        });
        return changed;
    }

    /// <summary>Replaces the value with <paramref name="update"/> of the current one (default when absent), atomically.</summary>
    public TValue AddOrUpdate(TKey key, Func<TValue?, TValue> update)
    {
        lock (_lock)
        {
            var current = _entries.Remove(key, out var existing) ? existing.Value.Value : default;
            if (existing != null) _order.Remove(existing);
            var value = update(current);
            _entries[key] = _order.AddFirst((key, value));
            if (_entries.Count > capacity)
            {
                var oldest = _order.Last!;
                _order.RemoveLast();
                _entries.Remove(oldest.Value.Key);
            }
            return value;
        }
    }
}
