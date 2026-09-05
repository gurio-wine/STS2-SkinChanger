namespace STS2SkinChanger.Core;

/// <summary>Stable package-owned keys; display names are resolved from the owning package.</summary>
internal static class BundlePresetPolicy
{
    private const string Prefix = "__bundle_preset__:";
    internal static string PresetKey(CharacterSkinBundle bundle) => Prefix + bundle.Id;
    internal static bool IsOwned(string key) => key.StartsWith(Prefix, StringComparison.Ordinal);

    internal static Dictionary<string, string> CardSelections(CardSkinPreset preset, string groupId) =>
        preset.AllOriginal ? new(StringComparer.OrdinalIgnoreCase) { ["cards:" + groupId] = "__base__" }
            : new(preset.Selections, StringComparer.OrdinalIgnoreCase);

    internal static Dictionary<string, string> MonsterSelections(MonsterSkinPreset preset, IEnumerable<string> groups) =>
        preset.AllOriginal ? groups.ToDictionary(id => id, _ => "__base__", StringComparer.OrdinalIgnoreCase)
            : new(preset.Selections, StringComparer.OrdinalIgnoreCase);

    internal static string DisplayName(SkinConfig config, string key) =>
        config.CharacterSkinBundles.FirstOrDefault(bundle => PresetKey(bundle) == key)?.Name ?? key;

    internal static IEnumerable<string> HiddenSources(SkinConfig config, string groupId) =>
        config.CharacterSkinBundles.Where(bundle => bundle.HideSources &&
            bundle.CharacterGroupId.Equals(groupId, StringComparison.OrdinalIgnoreCase) &&
            bundle.CharacterOptionId != "__base__")
        .Select(bundle => bundle.CharacterOptionId).Distinct(StringComparer.OrdinalIgnoreCase);

    internal static void Synchronize(SkinConfig config, IEnumerable<string> cardCategories,
        IEnumerable<string> monsterCategories)
    {
        var cards = cardCategories.ToArray();
        var monsters = monsterCategories.ToArray();
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bundle in config.CharacterSkinBundles)
        {
            if (!identities.Add(bundle.Id))
            {
                var previousKey = PresetKey(bundle);
                bundle.Id = Guid.NewGuid().ToString("N");
                identities.Add(bundle.Id);
                foreach (var references in new[] { bundle.CardPresetNames, bundle.MonsterPresetNames })
                    foreach (var category in references.Where(pair => pair.Value == previousKey).Select(pair => pair.Key).ToArray())
                        references[category] = PresetKey(bundle);
            }
            var key = PresetKey(bundle);
            foreach (var category in cards)
                if (!config.CardSkinPresets.Any(p => p.Name == key &&
                        string.Equals(p.CategoryId, category, StringComparison.OrdinalIgnoreCase)))
                    config.CardSkinPresets.Add(new CardSkinPreset
                        { Name = key, CategoryId = category, AllOriginal = true });
            foreach (var category in monsters)
                if (!config.MonsterSkinPresets.Any(p => p.Name == key &&
                        p.CategoryId.Equals(category, StringComparison.OrdinalIgnoreCase)))
                    config.MonsterSkinPresets.Add(new MonsterSkinPreset
                        { Name = key, CategoryId = category, AllOriginal = true });
        }
    }

    internal static void InitializeDraft(CharacterSkinBundle bundle, IEnumerable<string> cards, IEnumerable<string> monsters)
    {
        var key = PresetKey(bundle);
        foreach (var category in cards) bundle.CardPresetNames[category] = key;
        foreach (var category in monsters) bundle.MonsterPresetNames[category] = key;
    }

    internal static void RemoveOwnedPresets(SkinConfig config, CharacterSkinBundle bundle)
    {
        var key = PresetKey(bundle);
        config.CardSkinPresets.RemoveAll(p => p.Name == key);
        config.MonsterSkinPresets.RemoveAll(p => p.Name == key);
        foreach (var active in new[] { config.ActiveCardSkinPresets, config.ActiveMonsterSkinPresets })
            foreach (var category in active.Where(p => p.Value == key).Select(p => p.Key).ToArray()) active.Remove(category);
        foreach (var other in config.CharacterSkinBundles)
            foreach (var references in new[] { other.CardPresetNames, other.MonsterPresetNames })
                foreach (var category in references.Where(p => p.Value == key).Select(p => p.Key).ToArray()) references.Remove(category);
    }
}
