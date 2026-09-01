namespace STS2SkinChanger.Core;

/// <summary>
/// Keeps a baseline resource from hiding the selected provider's exported-resource redirect.
/// Other files in the same promoted PCK remain on the baseline unless their own group selected
/// them, so a multi-group package cannot leak one owner's skin into another owner.
/// </summary>
internal static class PromotedPackOverlayPolicy
{
    internal static IReadOnlyList<string> FindBaselinePathsShadowingSelectedRemaps(
        IEnumerable<string> baselinePaths,
        IEnumerable<string> selectedProviderOverlayPaths)
    {
        ArgumentNullException.ThrowIfNull(baselinePaths);
        ArgumentNullException.ThrowIfNull(selectedProviderOverlayPaths);

        var selectedRemaps = selectedProviderOverlayPaths
            .Where(path => path.EndsWith(".remap", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return baselinePaths
            .Where(path => selectedRemaps.Contains(path + ".remap"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
