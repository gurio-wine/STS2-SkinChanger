using STS2SkinChanger.Catalog;

namespace STS2SkinChanger.Core;

internal static partial class SkinService
{
    internal static bool IsRandomCharacterSkinEnabled(string groupId) =>
        Config.RandomCharacterSkinGroups.Contains(groupId, StringComparer.OrdinalIgnoreCase);

    internal static bool SetRandomCharacterSkinEnabled(string groupId, bool enabled)
    {
        lock (Sync)
        {
            if (IsRandomCharacterSkinEnabled(groupId) == enabled) return true;
            var next = Config.CloneForBundleTransaction();
            next.RandomCharacterSkinGroups.RemoveAll(id => id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
            if (enabled)
            {
                next.RandomCharacterSkinGroups.Add(groupId);
                next.ActiveCharacterSkinBundles.Remove(groupId);
            }
            // No pack warm-up, resource mount, preview callback, or multiplayer advertisement.
            return CommitBundleConfiguration(next, () => { }, () => { }, () => { });
        }
    }

    internal static void RestoreRandomCharacterSkinsForLobby()
    {
        lock (Sync)
        {
            // The saved run owns its actual random result. Only entering a new character
            // selection screen may normalize the lobby; never do this from a config getter,
            // a preview rebuild or while a run's temporary settings are still active.
            if (_characterSkinBundleRunState != null || _characterSkinBundleRunSnapshot != null) return;
            foreach (var groupId in Config.RandomCharacterSkinGroups.ToArray())
            {
                if (Catalog?.IsCharacterAppearanceGroup(groupId) != true) continue;
                var previous = Config.GetSelection(groupId);
                if (!previous.Equals(SkinCatalog.BaseOptionId, StringComparison.OrdinalIgnoreCase))
                {
                    if (!ApplySelection(groupId, SkinCatalog.BaseOptionId))
                    {
                        ModLog.Warn($"选角恢复随机皮肤的原皮失败，保留现状：{groupId}；{LastError}");
                        continue;
                    }
                    ModLog.Info($"选角随机皮肤已恢复原皮：{groupId}，上一局来源={previous}；独立对局记录保持不变。");
                }
                if (!ClearSelectedCharacterSkinBundle(groupId))
                    ModLog.Warn($"清除上局随机皮肤包标记失败：{groupId}；{LastError}");
            }
        }
    }

    private static void ApplyRandomCharacterSkinForNewRun(string groupId)
    {
        var visible = new[] { SkinCatalog.BaseOptionId }
            .Concat(GetCharacterSkinOptions(groupId).Select(option => option.Id))
            .Concat(GetCharacterSkinBundles(groupId)
                .Where(bundle => GetCharacterSkinBundleCharacterOption(groupId, bundle.Name) != null)
                .Select(bundle => CharacterSkinBundlePolicy.CreateSelectionOptionId(bundle.Name)));
        // Independent cosmetic RNG: never advance the game's seeded gameplay generators.
        var selected = RandomCharacterSkinPolicy.Draw(visible, System.Random.Shared.Next);
        var isBundle = CharacterSkinBundlePolicy.TryGetSelectionBundleName(selected, out var bundleName);
        var skin = isBundle ? GetCharacterSkinBundleCharacterOption(groupId, bundleName) : selected;
        if (!ClearSelectedCharacterSkinBundle(groupId)) return;
        if (skin == null || !ApplySelection(groupId, skin))
        {
            ModLog.Warn($"随机皮肤 {groupId}/{selected} 加载失败，回退原皮：{LastError}");
            ApplySelection(groupId, SkinCatalog.BaseOptionId);
            return;
        }
        if (isBundle && SelectCharacterSkinBundle(groupId, bundleName))
        {
            if (!ApplySelectedCharacterSkinBundleForRun(groupId, out var warnings))
                ModLog.Warn("随机皮肤包预设应用失败：" + LastError);
            foreach (var warning in warnings) ModLog.Warn("随机皮肤包：" + warning);
        }
        ModLog.Info($"本局随机皮肤：{groupId} -> {selected}，实际角色来源={Config.GetSelection(groupId)}。");
    }

    private static void ResumeRandomCharacterSkin(CharacterSkinBundleRunState record)
    {
        if (record.RandomCharacterOptionId is not { } selected) return;
        var resolved = Catalog?.ResolveStoredVisualSelectionId(record.CharacterGroupId, selected) ?? SkinCatalog.BaseOptionId;
        if (!ApplySelection(record.CharacterGroupId, resolved))
        {
            ModLog.Warn($"本局保存的随机皮肤 {selected} 已不可用，回退原皮，不重新抽取。");
            ApplySelection(record.CharacterGroupId, SkinCatalog.BaseOptionId);
        }
    }
}
