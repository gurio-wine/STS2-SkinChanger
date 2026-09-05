using System.Text.Json;

namespace STS2SkinChanger.Core;

internal sealed record CardPresetMigrationResult(int Split, int Archived);

/// <summary>Never invent a category snapshot from the current live priorities.</summary>
internal static class CardPresetMigrationPolicy
{
    internal static CardPresetMigrationResult Run(SkinConfig config,
        IReadOnlyDictionary<string, string> categories, IReadOnlyDictionary<string, string> cardGroups)
    {
        if (categories.Count == 0) return new(0, 0);
        var archived = config.CardPresetMigrationRepairVersion < 1 ? ArchiveRecognizableCopies(config, categories) : 0;
        config.CardPresetMigrationRepairVersion = 1;
        var legacyPresets = config.CardSkinPresets.Where(p => string.IsNullOrWhiteSpace(p.CategoryId)).ToArray();
        var migrated = config.CardSkinPresets.Where(p => !string.IsNullOrWhiteSpace(p.CategoryId)).ToList();
        var count = 0;
        bool Belongs(string key, string group) => key.Equals("cards:" + group, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(cardGroups.GetValueOrDefault(key), group, StringComparison.OrdinalIgnoreCase);
        foreach (var legacy in legacyPresets)
        {
            var remainder = legacy.Clone();
            foreach (var group in categories)
            {
                var selected = legacy.Selections.Where(p => Belongs(p.Key, group.Key))
                    .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
                legacy.CardSkinPriorities.TryGetValue(group.Key, out var entries);
                // An empty generated category is not evidence of a saved customization.
                // An explicit original-skin selection, on the other hand, is real saved data.
                if ((entries?.Count ?? 0) == 0 && selected.Count == 0)
                {
                    remainder.CardSkinPriorities.Remove(group.Key);
                    continue;
                }
                var name = group.Value + "-" + legacy.Name;
                var suffix = 2;
                while (migrated.Any(p => string.Equals(p.CategoryId, group.Key, StringComparison.OrdinalIgnoreCase) &&
                        p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    name = group.Value + "-" + legacy.Name + "-" + suffix++;
                migrated.Add(new CardSkinPreset
                {
                    Name = name, CategoryId = group.Key,
                    CardSkinPriorities = new(StringComparer.OrdinalIgnoreCase) { [group.Key] = entries?.ToList() ?? [] },
                    Selections = selected, AllOriginal = legacy.AllOriginal
                });
                remainder.CardSkinPriorities.Remove(group.Key);
                foreach (var key in selected.Keys) remainder.Selections.Remove(key);
                if (legacy.Name.Equals(config.ActiveCardSkinPreset, StringComparison.OrdinalIgnoreCase))
                    config.ActiveCardSkinPresets.TryAdd(group.Key, name);
                count++;
            }
            // Temporarily unavailable mod cards retain their original data for a later pass.
            if (remainder.CardSkinPriorities.Count > 0 || remainder.Selections.Count > 0) migrated.Add(remainder);
        }
        config.CardSkinPresets = migrated;
        if (!migrated.Any(p => string.IsNullOrWhiteSpace(p.CategoryId) && p.Name == config.ActiveCardSkinPreset))
            config.ActiveCardSkinPreset = null;
        return new(count, archived);
    }

    private static int ArchiveRecognizableCopies(SkinConfig config, IReadOnlyDictionary<string, string> categories)
    {
        // Old releases did not record provenance. A name alone is never enough. Require a
        // repeated identical fallback payload in one category, two distinct original names
        // still present elsewhere, and no individual-card override. Edited copies and
        // ambiguous single entries and package references are retained. Archives keep the complete original data.
        string? OriginalName(CardSkinPreset preset)
        {
            if (preset.CategoryId == null || preset.AllOriginal || BundlePresetPolicy.IsOwned(preset.Name) ||
                config.CharacterSkinBundles.Any(bundle => string.Equals(
                    bundle.CardPresetNames.GetValueOrDefault(preset.CategoryId), preset.Name, StringComparison.OrdinalIgnoreCase)) ||
                preset.Selections.Keys.Any(key => !key.Equals("cards:" + preset.CategoryId, StringComparison.OrdinalIgnoreCase)) ||
                preset.CardSkinPriorities.Keys.Any(key =>
                    !key.Equals(preset.CategoryId, StringComparison.OrdinalIgnoreCase))) return null;
            foreach (var prefix in new[] { preset.CategoryId, categories.GetValueOrDefault(preset.CategoryId) })
                if (!string.IsNullOrEmpty(prefix) && preset.Name.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase))
                {
                    var origin = preset.Name[(prefix.Length + 1)..];
                    if (origin.Length > 0 && config.CardSkinPresets.Any(other =>
                            !string.Equals(other.CategoryId, preset.CategoryId, StringComparison.OrdinalIgnoreCase) &&
                            other.Name.Equals(origin, StringComparison.OrdinalIgnoreCase))) return origin;
                }
            return null;
        }
        var candidates = config.CardSkinPresets.Select(preset => (Preset: preset, Origin: OriginalName(preset)))
            .Where(item => item.Origin != null)
            .GroupBy(item => item.Preset.CategoryId!.ToLowerInvariant() + "\n" +
                JsonSerializer.Serialize(item.Preset.CardSkinPriorities.Values.FirstOrDefault() ?? []) + "\n" +
                JsonSerializer.Serialize(item.Preset.Selections.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)))
            .Where(group => group.Select(item => item.Origin).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2)
            .SelectMany(group => group).Select(item => item.Preset).ToArray();
        foreach (var preset in candidates)
        {
            config.ArchivedLegacyCardSkinPresets.Add(preset.Clone());
            config.CardSkinPresets.Remove(preset);
            if (string.Equals(config.ActiveCardSkinPresets.GetValueOrDefault(preset.CategoryId!), preset.Name,
                    StringComparison.OrdinalIgnoreCase)) config.ActiveCardSkinPresets.Remove(preset.CategoryId!);
            foreach (var bundle in config.CharacterSkinBundles)
                CharacterSkinBundlePolicy.RemovePresetReference(bundle, false, preset.CategoryId!, preset.Name);
        }
        return candidates.Length;
    }
}
