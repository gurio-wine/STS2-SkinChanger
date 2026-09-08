namespace STS2SkinChanger.Core;

// Catalog membership may grow after a community refresh. Query only new IDs;
// cancellation belongs to the requesting panel, not to the shared Steam query.
internal sealed class WorkshopCatalogSession<T>
{
    private sealed class State
    {
        internal readonly SemaphoreSlim Gate = new(1);
        internal readonly HashSet<ulong> Attempted = [];
        internal Dictionary<ulong, T> Published = [];
    }
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);
    private State For(string language)
    {
        lock (_states)
        {
            if (!_states.TryGetValue(language, out var state)) _states[language] = state = new();
            return state;
        }
    }
    internal IReadOnlyDictionary<ulong, T> Cached(string language) => Volatile.Read(ref For(language).Published);
    internal Task<Dictionary<ulong, T>> Get(string language, ulong[] ids, Func<ulong[], Task<Dictionary<ulong, T>>> load, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var task = Load(For(language), ids, load);
        return token.CanBeCanceled ? task.WaitAsync(token) : task;
    }
    private static async Task<Dictionary<ulong, T>> Load(State state, ulong[] ids, Func<ulong[], Task<Dictionary<ulong, T>>> load)
    {
        await state.Gate.WaitAsync();
        try
        {
            var missing = ids.Distinct().Where(id => !state.Attempted.Contains(id)).ToArray();
            if (missing.Length == 0) return state.Published;
            var additions = await load(missing);
            var result = new Dictionary<ulong, T>(state.Published);
            var requested = missing.ToHashSet();
            foreach (var (id, value) in additions) if (requested.Contains(id)) result[id] = value;
            state.Attempted.UnionWith(missing);
            Volatile.Write(ref state.Published, result);
            return result;
        }
        finally { state.Gate.Release(); }
    }
}
