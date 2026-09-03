using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using STS2SkinChanger.Catalog;

namespace STS2SkinChanger.Core;

internal sealed record SkinPresetCategory(string Id, string DisplayName, IReadOnlyList<string> PresetNames);

internal static partial class SkinService
{
    public static IReadOnlyList<CharacterSkinBundle> GetCharacterSkinBundles(string groupId)
    {
        lock (Sync)
        {
            return Config.CharacterSkinBundles
                .Where(bundle => bundle.CharacterGroupId.Equals(groupId, StringComparison.OrdinalIgnoreCase))
                .Select(CharacterSkinBundlePolicy.Clone).ToArray();
        }
    }

    public static IReadOnlyList<SkinPresetCategory> GetCardPresetCategories()
    {
        lock (Sync)
        {
            return Catalog?.CardGroups.Select(group => new SkinPresetCategory(
                group.Id, group.DisplayName, GetCardSkinPresets(group.Id).Select(preset => preset.Name).ToArray()))
                .ToArray() ?? [];
        }
    }

    public static IReadOnlyList<SkinPresetCategory> GetMonsterPresetCategories()
    {
        lock (Sync)
        {
            var titles = ModelDb.Acts.ToDictionary(
                act => "act:" + act.Id.Entry.ToLowerInvariant(),
                act => act.Title.GetFormattedText(), StringComparer.OrdinalIgnoreCase);
            titles["events"] = new LocString("bestiary", "EVENTS.title").GetFormattedText();
            return Config.MonsterSkinCategoryGroups.Keys.Select(id => new SkinPresetCategory(
                id, titles.GetValueOrDefault(id, id), GetMonsterSkinPresets(id).Select(preset => preset.Name).ToArray()))
                .ToArray();
        }
    }

    public static bool CreateCharacterSkinBundle(CharacterSkinBundle draft) =>
        SaveCharacterSkinBundle(null, draft);

    public static bool OverwriteCharacterSkinBundle(string currentName, CharacterSkinBundle draft) =>
        SaveCharacterSkinBundle(currentName, draft);

    public static bool RenameCharacterSkinBundle(string groupId, string currentName, string newName)
    {
        lock (Sync)
        {
            var bundle = GetCharacterSkinBundles(groupId).FirstOrDefault(candidate =>
                candidate.Name.Equals(currentName, StringComparison.OrdinalIgnoreCase));
            if (bundle == null)
            {
                LastError = ModLocalization.Get(ModText.BundleUnavailable);
                return false;
            }
            bundle.Name = newName;
            return SaveCharacterSkinBundle(currentName, bundle);
        }
    }

    private static int FindCharacterSkinBundleIndex(string groupId, string name) =>
        Config.CharacterSkinBundles.FindIndex(bundle =>
            bundle.CharacterGroupId.Equals(groupId, StringComparison.OrdinalIgnoreCase) &&
            bundle.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool SaveCharacterSkinBundle(string? currentName, CharacterSkinBundle draft)
    {
        lock (Sync)
        {
            var normalized = CharacterSkinBundlePolicy.Normalize([draft]).SingleOrDefault();
            if (normalized == null || normalized.Name.Length > CardSkinPresetNameMaxLength ||
                normalized.Name.Any(char.IsControl) ||
                !CharacterSkinBundlePolicy.IsValidCharacterOptionReference(normalized.CharacterOptionId))
            {
                LastError = ModLocalization.Get(ModText.BundleInvalidName);
                return false;
            }
            var index = currentName == null ? -1 : FindCharacterSkinBundleIndex(normalized.CharacterGroupId, currentName);
            var duplicate = FindCharacterSkinBundleIndex(normalized.CharacterGroupId, normalized.Name);
            if (currentName != null && index < 0 || duplicate >= 0 && duplicate != index)
            {
                LastError = ModLocalization.Get(duplicate >= 0 ? ModText.BundleDuplicateName : ModText.BundleUnavailable);
                return false;
            }
            var next = Config.CloneForBundleTransaction();
            if (index < 0)
            {
                next.CharacterSkinBundles.Add(normalized);
            }
            else
            {
                next.CharacterSkinBundles[index] = normalized;
                if (string.Equals(next.ActiveCharacterSkinBundles.GetValueOrDefault(normalized.CharacterGroupId),
                        currentName, StringComparison.OrdinalIgnoreCase))
                {
                    next.ActiveCharacterSkinBundles[normalized.CharacterGroupId] = normalized.Name;
                }
            }
            return CommitBundleConfiguration(next, () => { }, () => { }, () => { });
        }
    }

    public static bool DeleteCharacterSkinBundle(string groupId, string name)
    {
        lock (Sync)
        {
            var index = FindCharacterSkinBundleIndex(groupId, name);
            if (index < 0)
            {
                LastError = ModLocalization.Get(ModText.BundleUnavailable);
                return false;
            }
            var next = Config.CloneForBundleTransaction();
            next.CharacterSkinBundles.RemoveAt(index);
            if (string.Equals(next.ActiveCharacterSkinBundles.GetValueOrDefault(groupId), name,
                    StringComparison.OrdinalIgnoreCase))
            {
                next.ActiveCharacterSkinBundles.Remove(groupId);
            }
            // Removing a saved bundle never undoes the player's current appearance.
            return CommitBundleConfiguration(next, () => { }, () => { }, () => { });
        }
    }

    public static bool SelectCharacterSkinBundle(string groupId, string name)
    {
        lock (Sync)
        {
            var index = FindCharacterSkinBundleIndex(groupId, name);
            if (index < 0)
            {
                LastError = ModLocalization.Get(ModText.BundleUnavailable);
                return false;
            }
            if (string.Equals(Config.ActiveCharacterSkinBundles.GetValueOrDefault(groupId),
                    Config.CharacterSkinBundles[index].Name, StringComparison.OrdinalIgnoreCase))
            {
                LastError = null;
                return true;
            }
            var next = Config.CloneForBundleTransaction();
            next.ActiveCharacterSkinBundles[groupId] = Config.CharacterSkinBundles[index].Name;
            return CommitBundleConfiguration(next, () => { }, () => { }, () => { });
        }
    }

    public static bool ClearSelectedCharacterSkinBundle(string groupId)
    {
        lock (Sync)
        {
            if (!Config.ActiveCharacterSkinBundles.ContainsKey(groupId))
            {
                LastError = null;
                return true;
            }
            var next = Config.CloneForBundleTransaction();
            next.ActiveCharacterSkinBundles.Remove(groupId);
            return CommitBundleConfiguration(next, () => { }, () => { }, () => { });
        }
    }

    public static bool ApplySelectedCharacterSkinBundleForRun(
        string groupId,
        out IReadOnlyList<string> warnings)
    {
        lock (Sync)
        {
            warnings = [];
            var name = Config.ActiveCharacterSkinBundles.GetValueOrDefault(groupId);
            if (string.IsNullOrWhiteSpace(name))
            {
                LastError = null;
                return true;
            }
            if (!CharacterSkinBundlePolicy.TryEnterApplication(
                    groupId, name, new HashSet<string>(StringComparer.OrdinalIgnoreCase), out var applications))
            {
                LastError = ModLocalization.Get(ModText.BundleScopeConflict);
                return false;
            }
            return ApplyCharacterSkinBundle(groupId, name, applications, out warnings);
        }
    }

    private static bool ApplyCharacterSkinBundle(
        string groupId,
        string name,
        IReadOnlySet<string> activeApplications,
        out IReadOnlyList<string> warnings)
    {
        lock (Sync)
        {
            var notices = new List<string>();
            warnings = notices;
            var catalog = Catalog;
            var index = FindCharacterSkinBundleIndex(groupId, name);
            if (catalog == null || index < 0)
            {
                LastError = ModLocalization.Get(ModText.BundleUnavailable);
                return false;
            }
            var bundle = CharacterSkinBundlePolicy.Clone(Config.CharacterSkinBundles[index]);
            if (!activeApplications.Contains(groupId.Trim() + "\n" + bundle.Name.Trim()) ||
                !CharacterSkinBundlePolicy.IsValidCharacterOptionReference(bundle.CharacterOptionId))
            {
                LastError = ModLocalization.Get(ModText.BundleScopeConflict);
                return false;
            }
            var original = Config;
            var next = original.CloneForBundleTransaction();
            var visualGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cardGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Prepare()
            {
                var requestedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { groupId };
                var characterGroup = catalog.Groups.FirstOrDefault(group =>
                    group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
                var optionId = catalog.ResolveStoredVisualSelectionId(groupId, bundle.CharacterOptionId);
                if (!optionId.Equals(SkinCatalog.BaseOptionId, StringComparison.OrdinalIgnoreCase) &&
                    characterGroup?.Options.Any(option => option.Id.Equals(optionId, StringComparison.OrdinalIgnoreCase)) != true)
                {
                    notices.Add(ModLocalization.Get(ModText.BundleMissingSkin));
                    optionId = SkinCatalog.BaseOptionId;
                }
                if (characterGroup != null)
                {
                    var updates = catalog.BuildVisualSelectionTransaction(groupId, optionId, Config.Selections);
                    foreach (var update in updates)
                    {
                        Config.Selections[update.Key] = update.Value;
                    }
                    visualGroups.UnionWith(updates.Keys);
                }
                else
                {
                    Config.Selections[groupId] = SkinCatalog.BaseOptionId;
                }
                foreach (var reference in bundle.CardPresetNames)
                {
                    var presetIndex = FindCardSkinPresetIndex(reference.Key, reference.Value);
                    var group = catalog.CardGroups.FirstOrDefault(candidate =>
                        candidate.Id.Equals(reference.Key, StringComparison.OrdinalIgnoreCase));
                    if (presetIndex < 0 || group == null)
                    {
                        notices.Add(string.Format(ModLocalization.Get(ModText.BundleMissingPreset), reference.Value));
                        continue;
                    }
                    ApplyCardPresetSettings(group, Config.CardSkinPresets[presetIndex]);
                    cardGroups.Add(group.Id);
                }
                foreach (var reference in bundle.MonsterPresetNames)
                {
                    var presetIndex = FindMonsterSkinPresetIndex(reference.Key, reference.Value);
                    if (presetIndex < 0 || !Config.MonsterSkinCategoryGroups.ContainsKey(reference.Key))
                    {
                        notices.Add(string.Format(ModLocalization.Get(ModText.BundleMissingPreset), reference.Value));
                        continue;
                    }
                    ApplyMonsterPresetSettings(Config.MonsterSkinPresets[presetIndex]);
                    requestedGroups.UnionWith(Config.MonsterSkinCategoryGroups[reference.Key]);
                    visualGroups.UnionWith(ApplyMonsterCategoryPriorityToSelections(reference.Key));
                }
                // An inseparable full-runtime provider can request other characters/regions.
                // Do not let such a dependency silently turn an "Unchanged" region into part
                // of this bundle, or let a later monster preset replace the requested character.
                var protectedGroups = Config.MonsterSkinCategoryGroups.Values.SelectMany(ids => ids)
                    .Concat(ModelDb.AllCharacters.Select(character => character.Id.Entry.ToLowerInvariant()));
                if (CharacterSkinBundlePolicy.ChangesOutsideRequestedGroups(
                        original.Selections, Config.Selections, protectedGroups, requestedGroups) ||
                    !Config.GetSelection(groupId).Equals(optionId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(ModLocalization.Get(ModText.BundleScopeConflict));
                }
                visualGroups.UnionWith(Config.Selections.Keys.Union(original.Selections.Keys, StringComparer.OrdinalIgnoreCase)
                    .Where(id => !id.StartsWith("cards:", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(Config.Selections.GetValueOrDefault(id), original.Selections.GetValueOrDefault(id),
                            StringComparison.OrdinalIgnoreCase)));
                foreach (var id in visualGroups)
                {
                    UpdateVisualProviderPriority(id, Config.GetSelection(id));
                }
                Config.ActiveCharacterSkinBundles[groupId] = bundle.Name;
            }

            void RefreshVisuals()
            {
                CharacterPreviewSelections.Clear();
                foreach (var id in visualGroups)
                {
                    ClearRuntimeResourceCache(id);
                }
                if (visualGroups.Count > 0)
                {
                    MountOverlay(visualGroups);
                }
            }

            void RefreshCards()
            {
                CardPreviewSelections.Clear();
                foreach (var id in cardGroups)
                {
                    ClearCardPortraitCache(id);
                }
                if (cardGroups.Count > 0)
                {
                    MountCardOverlay(cardGroups);
                }
            }

            void Restore()
            {
                // One failing provider must not prevent restoring the other resource family.
                var failures = FailureIsolatedActionRunner.Run(
                    [("visuals", RefreshVisuals), ("cards", RefreshCards)]);
                if (failures.Count > 0)
                {
                    throw new AggregateException(failures.Select(failure => failure.Exception));
                }
            }

            return CommitBundleConfiguration(next, Prepare, () => { RefreshVisuals(); RefreshCards(); }, Restore);
        }
    }

    private static bool CommitBundleConfiguration(SkinConfig next, Action prepare, Action refresh, Action restore)
    {
        var error = StagedConfigurationTransaction.Run(Config, next, value => Config = value,
            prepare, refresh, value => value.Save(ConfigPath), restore);
        LastError = error?.Message;
        if (error != null)
        {
            ModLog.Error("皮肤包操作失败，已恢复原配置：" + error);
        }
        return error == null;
    }

    private static void ApplyCardPresetSettings(CardSkinGroup group, CardSkinPreset preset)
    {
        if (preset.CardSkinPriorities.TryGetValue(group.Id, out var requestedPriority))
        {
            Config.CardSkinPriorities[group.Id] = requestedPriority.ToList();
        }
        else
        {
            Config.CardSkinPriorities.Remove(group.Id);
        }
        ReplaceCardSelectionsForGroup(group.Id, preset.Selections);
        Config.CardPriorityDefaultsVersion = 1;
        SetActiveCardSkinPreset(group.Id, preset.Name);
        GetCardPriorityEntriesInternal(group);
    }

    private static List<CharacterSkinBundle> UpdateBundlePresetReferences(
        bool monsterPreset, string categoryId, string currentName, string? newName)
    {
        var previous = Config.CharacterSkinBundles;
        Config.CharacterSkinBundles = previous.Select(CharacterSkinBundlePolicy.Clone).ToList();
        foreach (var bundle in Config.CharacterSkinBundles)
        {
            if (newName == null)
            {
                CharacterSkinBundlePolicy.RemovePresetReference(bundle, monsterPreset, categoryId, currentName);
            }
            else
            {
                CharacterSkinBundlePolicy.RenamePresetReference(bundle, monsterPreset, categoryId, currentName, newName);
            }
        }
        return previous;
    }

    public static (float X, float Y)? GetCharacterSkinBundlePosition()
    {
        lock (Sync)
        {
            EnsureConfigLoaded();
            return Config.CharacterSkinBundleX is { } x && Config.CharacterSkinBundleY is { } y &&
                   float.IsFinite(x) && float.IsFinite(y)
                ? (Math.Clamp(x, 0f, 1f), Math.Clamp(y, 0f, 1f)) : null;
        }
    }

    public static void SetCharacterSkinBundlePosition(float x, float y) =>
        SaveCharacterSkinBundlePosition(x, y);

    public static void ResetCharacterSkinBundlePosition() => SaveCharacterSkinBundlePosition(null, null);

    private static void SaveCharacterSkinBundlePosition(float? x, float? y)
    {
        lock (Sync)
        {
            EnsureConfigLoaded();
            var next = Config.CloneForBundleTransaction();
            next.CharacterSkinBundleX = x is { } px && float.IsFinite(px) ? Math.Clamp(px, 0f, 1f) : null;
            next.CharacterSkinBundleY = y is { } py && float.IsFinite(py) ? Math.Clamp(py, 0f, 1f) : null;
            CommitBundleConfiguration(next, () => { }, () => { }, () => { });
        }
    }
}
