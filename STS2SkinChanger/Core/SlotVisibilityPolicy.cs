namespace STS2SkinChanger.Core;

internal sealed record SlotVisibilitySelection(
    string GroupId, string ProviderId, string ToggleId, bool Hidden, string[] SourceSlots);

internal static class SlotVisibilityPolicy
{
    // Audited against the provider's separate select/combat/rest skeletons and atlas regions.
    // These are asset contracts, not fuzzy name matching or a Mod-ID-wide hiding rule. Faces,
    // cloth masks and weapon ornaments are deliberately not targets. Unknown rigs are untouched.
    private static readonly string[] RavenSelectSlots = [
        "tougu mianju 0", "tougu mianju 1", "tougu mianju 2", "mianju yanjing 0",
        "mianju yanjing 6", "mianju yanjing 2", "mianju yanjing 3", "mianju yanjing 4",
        "mianju yanjing 5", "mianju yanjing 1", "mianju yinying 0"];
    private static readonly string[] RavenCombatSlots = ["mianjv", "gumian", "ATK mianju yanjing", "dujiaoshou_27"];
    private static readonly string[] RavenRestSlots = ["mianju", "mianju  yanjing"];

    public static string[] ResolveSlots(IEnumerable<string> sourceSlots, IEnumerable<string> availableSlots)
    {
        var source = sourceSlots.ToHashSet(StringComparer.Ordinal);
        var available = availableSlots.ToHashSet(StringComparer.Ordinal);
        if (source.Count == 0) return [];
        if (source.IsSubsetOf(available)) return source.ToArray();
        if (!source.SetEquals(RavenSelectSlots)) return [];
        if (RavenCombatSlots.All(available.Contains) && available.Contains("head21") && available.Contains("ATK tou"))
            return RavenCombatSlots.ToArray();
        if (RavenRestSlots.All(available.Contains) && available.Contains("head") && available.Contains("kouzhao"))
            return RavenRestSlots.ToArray();
        return [];
    }

    public static SlotVisibilitySelection[] GetSelections(SkinConfig config, string groupId, string providerId) =>
        config.SlotVisibilitySelections.Where(state =>
            state.GroupId.Equals(groupId, StringComparison.OrdinalIgnoreCase) &&
            state.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase)).ToArray();

    public static string[] GetHiddenSourceSlots(SkinConfig config, string groupId, string providerId) =>
        GetSelections(config, groupId, providerId).Where(state => state.Hidden)
            .SelectMany(state => state.SourceSlots).Distinct(StringComparer.Ordinal).ToArray();

    public static List<SlotVisibilitySelection> Normalize(IEnumerable<SlotVisibilitySelection>? selections) =>
        (selections ?? []).Where(state => state != null &&
            !string.IsNullOrWhiteSpace(state.GroupId) && !string.IsNullOrWhiteSpace(state.ProviderId) &&
            !string.IsNullOrWhiteSpace(state.ToggleId) && state.SourceSlots is { Length: > 0 and <= 256 })
        .Select(state => state with { SourceSlots = state.SourceSlots
            .Where(slot => !string.IsNullOrWhiteSpace(slot)).Distinct(StringComparer.Ordinal).ToArray() })
        .Where(state => state.SourceSlots.Length > 0)
        .DistinctBy(state => (state.GroupId.ToLowerInvariant(), state.ProviderId.ToLowerInvariant(), state.ToggleId))
        .ToList();
}

/// <summary>Only owns alpha. Tracks animation writes without treating our previous zero as a new baseline.</summary>
internal sealed class SlotAlphaMask
{
    private float? _restoreAlpha;

    public float Hide(float currentAlpha)
    {
        if (_restoreAlpha == null || currentAlpha != 0f) _restoreAlpha = currentAlpha;
        return 0f;
    }

    public float Restore(float currentAlpha)
    {
        var result = currentAlpha == 0f ? _restoreAlpha ?? currentAlpha : currentAlpha;
        _restoreAlpha = null;
        return result;
    }
}
