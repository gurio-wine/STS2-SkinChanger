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
        RuntimeProviderScope scope)
    {
        var visibleGroups = scope.VisibleGroupIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return candidates
            .Where(candidate =>
                (scope.IncludeRunWideMonsterProviders && candidate.IsRunWideMonsterProvider) ||
                candidate.GroupIds.Any(visibleGroups.Contains))
            .Select(candidate => candidate.ProviderId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
