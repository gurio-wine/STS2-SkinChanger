namespace STS2SkinChanger.Core;

/// <summary>
/// A package that is not a skin can still override one part of a presentation the game already
/// ships: a style/UI overhaul that replaces the merchant room's hand animation is the working
/// example. Such a package keeps loading through the game's own loader, so its UI, audio, fonts
/// and initializer stay exactly as the author shipped them, while the overridden parts become
/// selectable skins like any other appearance. Nothing here looks at a Mod's name, Workshop ID or
/// author namespace.
/// </summary>
internal static class BaselinePartTakeoverPolicy
{
    /// <summary>
    /// The paths of one package that can serve as its selectable parts. A path qualifies only when
    /// it belongs to a group the game (or a loaded gameplay Mod) defines, is not that group's
    /// anchor — a package without an anchor is by definition not presenting the group — and exists
    /// in the baseline, because the original has to be handed back as soon as the player selects
    /// another option. Parts the author added under its own paths cannot qualify: there is no
    /// original to restore, so they can only stay with the author's own package.
    /// </summary>
    public static IReadOnlyList<string> ResolveServablePartPaths(
        IEnumerable<string> candidatePaths,
        Func<string, bool> belongsToKnownGroup,
        Func<string, bool> isGroupAnchor,
        Func<string, bool> existsInBaseline)
    {
        ArgumentNullException.ThrowIfNull(candidatePaths);
        var servable = new List<string>();
        foreach (var path in candidatePaths)
        {
            if (!belongsToKnownGroup(path) || isGroupAnchor(path) || !existsInBaseline(path))
            {
                continue;
            }

            servable.Add(path);
        }

        return servable;
    }

    /// <summary>
    /// A part is already what the player sees: the author's package is mounted and the game renders
    /// its art. So it stays the group's default until the player chooses something else, instead of
    /// being switched off by an untouched selection.
    /// </summary>
    public static string? ResolveAdoptedDefault(IReadOnlyList<string> partOptionIds)
    {
        ArgumentNullException.ThrowIfNull(partOptionIds);
        return partOptionIds.Count == 0
            ? null
            : partOptionIds.Order(StringComparer.OrdinalIgnoreCase).First();
    }
}
