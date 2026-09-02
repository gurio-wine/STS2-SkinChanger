namespace STS2SkinChanger.Core;

internal static class VisualSelectionOverlayPolicy
{
    internal static IReadOnlyDictionary<string, string> Merge(
        IReadOnlyDictionary<string, string> persistentSelections,
        IReadOnlyDictionary<string, string>? previewSelections,
        IReadOnlyDictionary<string, string>? scopedSelections)
    {
        if ((previewSelections == null || previewSelections.Count == 0) &&
            (scopedSelections == null || scopedSelections.Count == 0))
        {
            return persistentSelections;
        }

        var merged = new Dictionary<string, string>(
            persistentSelections,
            StringComparer.OrdinalIgnoreCase);
        Overlay(merged, previewSelections);
        // A multiplayer creature/player scope describes the concrete owner currently being
        // instantiated. It must remain authoritative even while the local player previews a skin.
        Overlay(merged, scopedSelections);
        return merged;
    }

    internal static IReadOnlySet<string> AffectedGroups(
        IEnumerable<string> previousPreviewGroups,
        IEnumerable<string> nextPreviewGroups) =>
        previousPreviewGroups
            .Concat(nextPreviewGroups)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static void Overlay(
        IDictionary<string, string> target,
        IReadOnlyDictionary<string, string>? overlay)
    {
        if (overlay == null)
        {
            return;
        }

        foreach (var pair in overlay)
        {
            target[pair.Key] = pair.Value;
        }
    }
}
