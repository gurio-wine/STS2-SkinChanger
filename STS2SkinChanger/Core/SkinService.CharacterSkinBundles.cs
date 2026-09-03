using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using STS2SkinChanger.Catalog;

namespace STS2SkinChanger.Core;

internal sealed record SkinPresetCategory(string Id, string DisplayName, IReadOnlyList<string> PresetNames);

internal static partial class SkinService
{
    private static SkinConfig? _characterSkinBundleRunSnapshot;
    private static HashSet<string> _characterSkinBundleRunVisualGroups =
        new(StringComparer.OrdinalIgnoreCase);
    private static HashSet<string> _characterSkinBundleRunCardGroups =
        new(StringComparer.OrdinalIgnoreCase);

    private static string CharacterSkinBundleRunSnapshotPath =>
        Path.Combine(OS.GetUserDataDir(), "skin_changer_bundle_run_restore.json");

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

    public static string? GetCharacterSkinBundleCharacterOption(string groupId, string name)
    {
        lock (Sync)
        {
            var index = FindCharacterSkinBundleIndex(groupId, name);
            var catalog = Catalog;
            if (index < 0 || catalog == null)
            {
                LastError = ModLocalization.Get(ModText.BundleUnavailable);
                return null;
            }

            var optionId = catalog.ResolveStoredVisualSelectionId(
                groupId,
                Config.CharacterSkinBundles[index].CharacterOptionId);
            var group = catalog.Groups.FirstOrDefault(candidate =>
                candidate.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
            if (!optionId.Equals(SkinCatalog.BaseOptionId, StringComparison.OrdinalIgnoreCase) &&
                group?.Options.Any(option => option.Id.Equals(
                    optionId, StringComparison.OrdinalIgnoreCase)) != true)
            {
                LastError = ModLocalization.Get(ModText.BundleMissingSkin);
                return null;
            }

            LastError = null;
            return optionId;
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
            return BeginCharacterSkinBundleRunSession(groupId, name, applications, out warnings);
        }
    }

    private static bool BeginCharacterSkinBundleRunSession(
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

            // A previous run should normally have reached RunManager.CleanUp. Restore it here as
            // a defensive boundary before a new run is staged, so a failed/aborted transition can
            // never make two packages recursively layer over one another.
            if (_characterSkinBundleRunSnapshot != null)
            {
                RestoreCharacterSkinBundleAfterRun();
            }

            var original = Config;
            var next = original.CloneForBundleTransaction();
            var visualGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cardGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Prepare()
            {
                var requestedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                // A run package may only touch the explicitly referenced monster regions. Most
                // importantly, the character is intentionally absent here: it was already
                // applied through the normal character selector path. Re-mounting character and
                // CZN-style multi-region packs in one transaction can replace the canonical
                // NCreatureVisuals scene with a provider-private Node2D scene.
                var protectedGroups = Config.MonsterSkinCategoryGroups.Values.SelectMany(ids => ids)
                    .Concat(ModelDb.AllCharacters.Select(character => character.Id.Entry.ToLowerInvariant()));
                if (CharacterSkinBundlePolicy.ChangesOutsideRequestedGroups(
                        original.Selections, Config.Selections, protectedGroups, requestedGroups) ||
                    !Config.GetSelection(groupId).Equals(
                        original.GetSelection(groupId), StringComparison.OrdinalIgnoreCase))
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

            // Persist the restore point before any temporary selection is mounted. During the run
            // the in-memory Config contains package presets, while the normal config file keeps
            // the player's original presets. The sidecar also repairs an interrupted/Alt-F4 run
            // on the next startup.
            original.Save(CharacterSkinBundleRunSnapshotPath);
            var error = StagedConfigurationTransaction.Run(
                original,
                next,
                value => Config = value,
                Prepare,
                () => { RefreshVisuals(); RefreshCards(); },
                _ => original.Save(ConfigPath),
                Restore);
            LastError = error?.Message;
            if (error != null)
            {
                DeleteCharacterSkinBundleRunSnapshot();
                ModLog.Error("开始对局前应用皮肤包预设失败，已恢复原配置：" + error);
                return false;
            }

            _characterSkinBundleRunSnapshot = original;
            _characterSkinBundleRunVisualGroups = visualGroups;
            _characterSkinBundleRunCardGroups = cardGroups;
            ModLog.Info(
                $"已为本局临时应用皮肤包“{bundle.Name}”：" +
                $"角色皮肤保持当前热切换结果，卡牌分类={cardGroups.Count}，" +
                $"怪物分组={visualGroups.Count}；离开本局时恢复原预设。");
            return true;
        }
    }

    public static void RestoreCharacterSkinBundleAfterRun()
    {
        lock (Sync)
        {
            var snapshot = _characterSkinBundleRunSnapshot;
            if (snapshot == null)
            {
                return;
            }

            var visualGroups = _characterSkinBundleRunVisualGroups;
            var cardGroups = _characterSkinBundleRunCardGroups;
            Config = snapshot;
            CharacterPreviewSelections.Clear();
            CardPreviewSelections.Clear();
            foreach (var id in visualGroups)
            {
                ClearRuntimeResourceCache(id);
            }
            foreach (var id in cardGroups)
            {
                ClearCardPortraitCache(id);
            }

            var failures = FailureIsolatedActionRunner.Run([
                ("visuals", () =>
                {
                    if (visualGroups.Count > 0)
                    {
                        MountOverlay(visualGroups);
                    }
                }),
                ("cards", () =>
                {
                    if (cardGroups.Count > 0)
                    {
                        MountCardOverlay(cardGroups);
                    }
                })
            ]);
            Config.Save(ConfigPath);
            DeleteCharacterSkinBundleRunSnapshot();
            _characterSkinBundleRunSnapshot = null;
            _characterSkinBundleRunVisualGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _characterSkinBundleRunCardGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (failures.Count == 0)
            {
                ModLog.Info("已在离开本局时恢复皮肤包应用前的卡牌与怪物预设。");
                return;
            }

            ModLog.Error("恢复皮肤包应用前预设时有资源刷新失败：" +
                         new AggregateException(failures.Select(failure => failure.Exception)));
        }
    }

    private static void DeleteCharacterSkinBundleRunSnapshot()
    {
        foreach (var path in new[]
                 {
                     CharacterSkinBundleRunSnapshotPath,
                     CharacterSkinBundleRunSnapshotPath + ".bak"
                 })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception exception)
            {
                ModLog.Warn("清理皮肤包本局恢复点失败，将在下次启动重试：" + exception.Message);
            }
        }
    }

    internal static SkinConfig RecoverInterruptedCharacterSkinBundleSession(SkinConfig current)
    {
        if (!File.Exists(CharacterSkinBundleRunSnapshotPath))
        {
            return current;
        }

        try
        {
            var restored = SkinConfig.Load(CharacterSkinBundleRunSnapshotPath);
            restored.Save(ConfigPath);
            DeleteCharacterSkinBundleRunSnapshot();
            ModLog.Info("检测到上次游戏在皮肤包生效期间退出，已恢复进入该局前的预设。");
            return restored;
        }
        catch (Exception exception)
        {
            ModLog.Warn("恢复上次皮肤包本局预设失败，将保留现有配置：" + exception.Message);
            return current;
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
