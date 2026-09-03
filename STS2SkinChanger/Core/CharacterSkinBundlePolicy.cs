namespace STS2SkinChanger.Core;

internal sealed class CharacterSkinBundle
{
    public string Name { get; set; } = string.Empty;
    public string CharacterGroupId { get; set; } = string.Empty;
    public string CharacterOptionId { get; set; } = "__base__";
    public Dictionary<string, string> CardPresetNames { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> MonsterPresetNames { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

internal static class CharacterSkinBundlePolicy
{
    internal static bool ChangesOutsideRequestedGroups(
        IReadOnlyDictionary<string, string> original, IReadOnlyDictionary<string, string> staged,
        IEnumerable<string> protectedGroups, IReadOnlySet<string> requestedGroups) =>
        protectedGroups.Any(id => !requestedGroups.Contains(id) &&
            !string.Equals(original.GetValueOrDefault(id, "__base__"), staged.GetValueOrDefault(id, "__base__"),
                StringComparison.OrdinalIgnoreCase));

    internal static List<CharacterSkinBundle> Normalize(IEnumerable<CharacterSkinBundle>? bundles)
    {
        return (bundles ?? [])
            .Where(bundle => bundle != null &&
                             !string.IsNullOrWhiteSpace(bundle.Name) &&
                             !string.IsNullOrWhiteSpace(bundle.CharacterGroupId))
            .Select(bundle => new CharacterSkinBundle
            {
                Name = bundle.Name.Trim(),
                CharacterGroupId = bundle.CharacterGroupId.Trim().ToLowerInvariant(),
                CharacterOptionId = string.IsNullOrWhiteSpace(bundle.CharacterOptionId)
                    ? "__base__"
                    : bundle.CharacterOptionId.Trim(),
                CardPresetNames = NormalizeReferences(bundle.CardPresetNames),
                MonsterPresetNames = NormalizeReferences(bundle.MonsterPresetNames)
            })
            .DistinctBy(
                bundle => bundle.CharacterGroupId + "\n" + bundle.Name,
                StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static void RenamePresetReference(
        CharacterSkinBundle bundle,
        bool monsterPreset,
        string categoryId,
        string previousName,
        string newName)
    {
        var references = monsterPreset ? bundle.MonsterPresetNames : bundle.CardPresetNames;
        if (references.TryGetValue(categoryId, out var current) &&
            current.Equals(previousName, StringComparison.OrdinalIgnoreCase))
        {
            references[categoryId] = newName;
        }
    }

    internal static void RemovePresetReference(
        CharacterSkinBundle bundle,
        bool monsterPreset,
        string categoryId,
        string presetName)
    {
        var references = monsterPreset ? bundle.MonsterPresetNames : bundle.CardPresetNames;
        if (references.TryGetValue(categoryId, out var current) &&
            current.Equals(presetName, StringComparison.OrdinalIgnoreCase))
        {
            references.Remove(categoryId);
        }
    }

    internal static CharacterSkinBundle Clone(CharacterSkinBundle bundle) =>
        new()
        {
            Name = bundle.Name,
            CharacterGroupId = bundle.CharacterGroupId,
            CharacterOptionId = bundle.CharacterOptionId,
            CardPresetNames = new Dictionary<string, string>(
                bundle.CardPresetNames, StringComparer.OrdinalIgnoreCase),
            MonsterPresetNames = new Dictionary<string, string>(
                bundle.MonsterPresetNames, StringComparer.OrdinalIgnoreCase)
        };

    private static Dictionary<string, string> NormalizeReferences(
        IReadOnlyDictionary<string, string>? references) =>
        (references ?? new Dictionary<string, string>())
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) &&
                           !string.IsNullOrWhiteSpace(pair.Value))
            .DistinctBy(pair => pair.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                pair => pair.Key.Trim().ToLowerInvariant(),
                pair => pair.Value.Trim(),
                StringComparer.OrdinalIgnoreCase);
}

// Persistence is the final step. A failed preparation never touches resources; a failed
// refresh or save restores the original configuration before rebuilding its resources.
internal static class StagedConfigurationTransaction
{
    internal static Exception? Run<T>(
        T original, T staged, Action<T> setCurrent, Action prepare,
        Action refresh, Action<T> persist, Action restoreResources)
    {
        var refreshStarted = false;
        setCurrent(staged);
        try
        {
            prepare();
            refreshStarted = true;
            refresh();
            persist(staged);
            return null;
        }
        catch (Exception error)
        {
            setCurrent(original);
            if (refreshStarted)
            {
                try
                {
                    restoreResources();
                }
                catch (Exception restoreError)
                {
                    return new AggregateException(error, restoreError);
                }
            }
            return error;
        }
    }
}
