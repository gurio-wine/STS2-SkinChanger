namespace STS2SkinChanger.Core;

// One producer per key for this process. Cancelling a page stops only that page's
// await, not the shared operation or its result (including an offline result).
internal sealed class WorkshopSessionCache<T>
{
    private readonly Dictionary<string, Lazy<Task<T>>> _entries = new(StringComparer.Ordinal);
    public Task<T> Get(string key, Func<Task<T>> load, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Lazy<Task<T>> entry;
        lock (_entries)
        {
            if (!_entries.TryGetValue(key, out entry!))
                _entries[key] = entry = new(load, LazyThreadSafetyMode.ExecutionAndPublication);
        }
        var pending = entry.Value;
        return token.CanBeCanceled ? pending.WaitAsync(token) : pending;
    }
    public bool TryGet(string key, out T value)
    {
        lock (_entries)
        {
            if (_entries.TryGetValue(key, out var entry) && entry.IsValueCreated && entry.Value.IsCompletedSuccessfully)
            { value = entry.Value.Result; return true; }
        }
        value = default!;
        return false;
    }
}
