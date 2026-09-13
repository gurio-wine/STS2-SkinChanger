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

    /// <summary>
    /// Whether an option stays a supplement for its character instead of an appearance the player
    /// can switch to.
    /// <para>
    /// A package that replaces the character's own visual creation draws the character itself from
    /// its own files at runtime. The canonical paths it happens to ship (a character-select image, a
    /// top-panel icon) are then a side effect of that package, not the whole of what it presents, so
    /// it must not be reduced to an icon-only pack: doing so holds back the code that draws the
    /// character and keeps the package's files unmounted, which leaves the game original on screen
    /// even while the player has selected it.
    /// </para>
    /// </summary>
    public static bool IsIconOnlySupplement(
        bool presentsOnlySupplementAssets,
        bool replacesCharacterVisualsAtRuntime) =>
        presentsOnlySupplementAssets && !replacesCharacterVisualsAtRuntime;
}
