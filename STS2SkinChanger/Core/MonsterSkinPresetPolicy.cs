namespace STS2SkinChanger.Core;

internal sealed record MonsterSkinPresetPriorityState(string OptionId, bool Enabled);

internal sealed record MonsterSkinPresetSnapshot(
    string CategoryId,
    IReadOnlyList<MonsterSkinPresetPriorityState> Priority,
    IReadOnlyDictionary<string, string> Selections);

internal static class MonsterSkinPresetPolicy
{
    internal static MonsterSkinPresetSnapshot Capture(
        string categoryId,
        IReadOnlyCollection<string> categoryGroupIds,
        IReadOnlyList<MonsterSkinPresetPriorityState> priority,
        IReadOnlyDictionary<string, string> selections)
    {
        var scopedIds = categoryGroupIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new MonsterSkinPresetSnapshot(
            categoryId,
            priority.ToArray(),
            selections
                .Where(pair => scopedIds.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));
    }

    internal static Dictionary<string, string> Apply(
        MonsterSkinPresetSnapshot preset,
        IReadOnlyDictionary<string, string> currentSelections)
    {
        var result = new Dictionary<string, string>(currentSelections, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in preset.Selections)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }
}
