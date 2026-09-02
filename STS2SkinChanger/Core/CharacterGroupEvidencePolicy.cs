namespace STS2SkinChanger.Core;

/// <summary>
/// Keeps weak character supplements (portraits, map markers and top-panel icons) from turning
/// into unrelated full character skins. A provider with no model anchor remains a valid icon-only
/// pack, while a provider that does contain models is assigned only to the anchored characters.
/// </summary>
internal static class CharacterGroupEvidencePolicy
{
    public static IReadOnlySet<string> ResolveEligibleGroups(
        IEnumerable<string> candidateGroupIds,
        IEnumerable<string> anchoredGroupIds)
    {
        var candidates = candidateGroupIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var anchors = anchoredGroupIds
            .Where(candidates.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return anchors.Count == 0 ? candidates : anchors;
    }
}
