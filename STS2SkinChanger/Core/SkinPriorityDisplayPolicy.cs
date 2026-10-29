namespace STS2SkinChanger.Core;

/// <summary>One row of a saved skin-priority list, as the player left it.</summary>
internal sealed record SkinPriorityState(string OptionId, bool Enabled);

/// <summary>
/// The one projection every player-facing skin list uses. Skins ticked on a skin-priority screen
/// come first in their saved manual order; the unticked ones follow, still in that saved order so
/// the lower half stays stable; skins no priority list has recorded keep their original tail.
/// A group without a priority screen keeps the order it was given.
/// </summary>
internal static class SkinPriorityDisplayPolicy
{
    /// <summary>
    /// Alpha of a skin the player has not ticked, so a list shows at a glance which sources the
    /// category actually uses. Clearly dimmer than a ticked row, still readable.
    /// </summary>
    internal const float UncheckedAlpha = 0.55f;

    private const int RankedEnabled = 0;
    private const int RankedDisabled = 1;
    private const int Unranked = 2;

    internal static IReadOnlyList<SkinPriorityState> States<T>(
        IEnumerable<T> entries,
        Func<T, string> optionId,
        Func<T, bool> enabled) =>
        entries.Select(entry => new SkinPriorityState(optionId(entry), enabled(entry))).ToArray();

    internal static IReadOnlyList<T> Project<T>(
        IReadOnlyList<T> options,
        Func<T, string> optionId,
        IReadOnlyList<SkinPriorityState> entries)
    {
        if (options.Count == 0 || entries.Count == 0)
        {
            return options;
        }

        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in options)
        {
            available.Add(optionId(option));
        }

        // Only recorded ids the list actually offers take part; everything else stays unranked.
        var rank = new Dictionary<string, (int Rank, int Order)>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (string.IsNullOrWhiteSpace(entry.OptionId) || !available.Contains(entry.OptionId))
            {
                continue;
            }

            rank.TryAdd(entry.OptionId, (entry.Enabled ? RankedEnabled : RankedDisabled, index));
        }

        // OrderBy is stable, so equal keys and unranked options keep the caller's order and the
        // projection can never add, drop or duplicate a member.
        return options
            .Select((option, index) => (Option: option, Index: index))
            .OrderBy(candidate => rank.TryGetValue(optionId(candidate.Option), out var key) ? key.Rank : Unranked)
            .ThenBy(candidate => rank.TryGetValue(optionId(candidate.Option), out var key) ? key.Order : candidate.Index)
            .Select(candidate => candidate.Option)
            .ToArray();
    }

    /// <summary>
    /// True only for a skin a priority list has recorded as not ticked. Rows which are not skins at
    /// all ("follow category", "game original", the workshop entry) and skins no list has seen yet
    /// are not unchecked and keep their normal appearance.
    /// </summary>
    internal static bool IsUnchecked(IReadOnlyList<SkinPriorityState>? entries, string optionId)
    {
        if (entries == null || string.IsNullOrWhiteSpace(optionId))
        {
            return false;
        }

        foreach (var entry in entries)
        {
            if (entry.OptionId.Equals(optionId, StringComparison.OrdinalIgnoreCase))
            {
                return !entry.Enabled;
            }
        }

        return false;
    }

    /// <summary>
    /// <see cref="IsUnchecked"/> as a reusable per-row lookup. A list is redrawn on every popup and
    /// every theme change, so the recorded ids are hashed once instead of scanned per item.
    /// </summary>
    internal static Func<string, bool> UncheckedLookup(IReadOnlyList<SkinPriorityState>? entries)
    {
        if (entries == null || entries.Count == 0)
        {
            return _ => false;
        }

        var recorded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uncheckedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.OptionId))
            {
                continue;
            }

            recorded.Add(entry.OptionId);
            if (!entry.Enabled)
            {
                uncheckedIds.Add(entry.OptionId);
            }
        }

        return optionId => !string.IsNullOrWhiteSpace(optionId) &&
                           recorded.Contains(optionId) &&
                           uncheckedIds.Contains(optionId);
    }

    /// <summary>
    /// Alpha multiplier of one skin row: a skin the category switched off fades, a skin the player
    /// excluded from the random pool fades further. Both apply together, so neither hides the other.
    /// </summary>
    internal static float RowAlpha(bool excludedFromRandom, bool uncheckedForCategory) =>
        (excludedFromRandom ? .5f : 1f) * (uncheckedForCategory ? UncheckedAlpha : 1f);
}
