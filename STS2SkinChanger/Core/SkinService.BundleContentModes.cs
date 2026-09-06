using STS2SkinChanger.Catalog;

namespace STS2SkinChanger.Core;

internal sealed record BundleModSource(string OptionId, string Name);

internal static partial class SkinService
{
    internal static IReadOnlyList<BundleModSource> GetBundleModSources(bool monsters)
    {
        lock (Sync)
        {
            var sources = monsters
                ? GetBundleMonsterCategoryIds().SelectMany(GetMonsterCategoryOptionsInternal)
                    .Select(option => new BundleModSource(option.OptionId, option.Name))
                : GetBundleCardGroups().SelectMany(group => group.Options)
                    .Select(option => new BundleModSource(option.Id, option.Name));
            return sources.DistinctBy(source => source.OptionId, StringComparer.OrdinalIgnoreCase)
                .OrderBy(source => source.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
    }

    internal static List<BundleModPriorityEntry> GetBundleModPriority(CharacterSkinBundle bundle, bool monsters)
    {
        lock (Sync)
        {
            string Resolve(string id)
            {
                if (monsters)
                {
                    foreach (var category in GetBundleMonsterCategoryIds())
                    {
                        var resolved = ResolveStoredMonsterPriorityOptionId(category, id);
                        if (!resolved.Equals(id, StringComparison.OrdinalIgnoreCase)) return resolved;
                    }
                }
                else if (Catalog is { } catalog)
                {
                    foreach (var group in GetBundleCardGroups())
                    {
                        var resolved = catalog.ResolveStoredCardSelectionId(group.Id, id);
                        if (!resolved.Equals(id, StringComparison.OrdinalIgnoreCase)) return resolved;
                    }
                }
                return id;
            }
            var stored = monsters ? bundle.MonsterModPriority : bundle.CardModPriority;
            return CharacterSkinBundlePolicy.CompletePriority(
                CharacterSkinBundlePolicy.NormalizePriority(stored).Select(entry => entry with { OptionId = Resolve(entry.OptionId) }),
                GetBundleModSources(monsters).Select(source => source.OptionId));
        }
    }

    // Compile the active mode to the same run-scoped preset snapshots used by save/resume.
    // These are not saved to the user's preset library and do not mutate the bundle.
    private static List<CardSkinPreset> ResolveBundleCardPresets(CharacterSkinBundle bundle)
    {
        if (bundle.CardMode != BundleContentMode.ModPriority)
        {
            var categories = GetBundleCardGroups().Select(group => group.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return bundle.CardPresetNames.Where(pair => categories.Contains(pair.Key))
                .Select(pair => FindCardSkinPresetIndex(pair.Key, pair.Value)).Where(index => index >= 0)
                .Select(index => Config.CardSkinPresets[index].Clone()).ToList();
        }
        var priority = GetBundleModPriority(bundle, monsters: false);
        return GetBundleCardGroups().Select(group =>
        {
            var entries = CharacterSkinBundlePolicy.ProjectPriority(priority, group.Options.Select(option => option.Id));
            return new CardSkinPreset
            {
                CategoryId = group.Id,
                CardSkinPriorities = new(StringComparer.OrdinalIgnoreCase)
                {
                    [group.Id] = entries.Select(entry => new CardSkinPriorityEntry(entry.OptionId, entry.Enabled)).ToList()
                },
                Selections = new(StringComparer.OrdinalIgnoreCase)
                {
                    [CardSelectionKey(group.Id)] = entries.FirstOrDefault(entry => entry.Enabled)?.OptionId ?? SkinCatalog.BaseOptionId
                }
            };
        }).ToList();
    }

    private static List<MonsterSkinPreset> ResolveBundleMonsterPresets(CharacterSkinBundle bundle)
    {
        if (bundle.MonsterMode != BundleContentMode.ModPriority)
        {
            var categories = GetBundleMonsterCategoryIds().ToHashSet(StringComparer.OrdinalIgnoreCase);
            return bundle.MonsterPresetNames.Where(pair => categories.Contains(pair.Key))
                .Select(pair => FindMonsterSkinPresetIndex(pair.Key, pair.Value)).Where(index => index >= 0)
                .Select(index => Config.MonsterSkinPresets[index].Clone()).ToList();
        }
        var priority = GetBundleModPriority(bundle, monsters: true);
        return GetBundleMonsterCategoryIds().Select(category =>
        {
            var groups = Config.MonsterSkinCategoryGroups[category].Where(id => Catalog!.Groups.Any(group =>
                group.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var entries = CharacterSkinBundlePolicy.ProjectPriority(priority,
                GetMonsterCategoryOptionsInternal(category).Select(option => option.OptionId));
            return new MonsterSkinPreset
            {
                CategoryId = category,
                Priority = entries.Select(entry => new MonsterSkinPriorityEntry(entry.OptionId, entry.Enabled)).ToList(),
                Selections = groups.ToDictionary(id => id, _ => SkinCatalog.BaseOptionId, StringComparer.OrdinalIgnoreCase),
                FollowingGroupIds = groups.ToList()
            };
        }).ToList();
    }
}
