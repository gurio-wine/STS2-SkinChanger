namespace STS2SkinChanger.Core;

internal sealed class CharacterSkinBundle
{
    public string Name { get; set; } = string.Empty;
    public string CharacterGroupId { get; set; } = string.Empty;
    public string CharacterOptionId { get; set; } = "base";
    public Dictionary<string, string> CardPresetNames { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> MonsterPresetNames { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

internal static class CharacterSkinBundlePolicy
{
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
                    ? "base"
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
            .ToDictionary(
                pair => pair.Key.Trim().ToLowerInvariant(),
                pair => pair.Value.Trim(),
                StringComparer.OrdinalIgnoreCase);
}
