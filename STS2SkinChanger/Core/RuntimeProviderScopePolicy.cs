namespace STS2SkinChanger.Core;

internal sealed record RuntimeProviderCandidate(
    string ProviderId,
    IReadOnlyCollection<string> GroupIds,
    bool IsRunWideMonsterProvider);

internal sealed record RuntimeProviderScope(
    IReadOnlyCollection<string> VisibleGroupIds,
    bool IncludeRunWideMonsterProviders);

internal static class RuntimeProviderScopePolicy
{
    public static IReadOnlySet<string> SelectActiveProviders(
        IEnumerable<RuntimeProviderCandidate> candidates,
        RuntimeProviderScope? scope)
    {
        var available = candidates.ToArray();
        if (scope == null)
        {
            // Before the game has created any screen/room scope, complete each selected provider's
            // one-time initialization under the normal pre-asset lifecycle. Delaying that first
            // initialization until character select or combat is unsafe for providers that
            // register Godot scene classes or fill static presentation registries: their scenes
            // can already have been cached as plain Node2D nodes by then. Once a real scope is
            // established, only visible providers remain active.
            return available
                .Select(candidate => candidate.ProviderId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var visibleGroups = scope.VisibleGroupIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return available
            .Where(candidate =>
                (scope.IncludeRunWideMonsterProviders && candidate.IsRunWideMonsterProvider) ||
                candidate.GroupIds.Any(visibleGroups.Contains))
            .Select(candidate => candidate.ProviderId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}

internal sealed class RuntimeProviderScopeLeaseTracker
{
    private long _current;

    public long Current => _current;

    public long Claim()
    {
        _current++;
        if (_current == 0)
        {
            _current++;
        }

        return _current;
    }

    public bool IsCurrent(long lease) => lease != 0 && lease == _current;

    public void Reset() => _current = 0;
}
