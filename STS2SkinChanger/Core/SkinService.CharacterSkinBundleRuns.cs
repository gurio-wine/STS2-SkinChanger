using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace STS2SkinChanger.Core;

internal static partial class SkinService
{
    private static CharacterSkinBundleRunState? _characterSkinBundleRunState;
    private static string? _characterSkinBundleRunSavePath;

    private static string GetCharacterSkinBundleRunSavePath(bool multiplayer)
    {
        var slot = SaveManager.Instance.GetProfileScopedPath("skin-changer") + (multiplayer ? ":mp" : ":sp");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(slot)));
        return Path.Combine(OS.GetUserDataDir(), "skin_changer_bundle_runs", key + ".json");
    }

    private static void CaptureCharacterSkinBundleRunPresets()
    {
        var state = _characterSkinBundleRunState;
        if (state == null || _characterSkinBundleRunSnapshot == null) return;
        state.Cards = state.Cards.Select(preset =>
            Catalog?.CardGroups.Any(group => group.Id.Equals(preset.CategoryId, StringComparison.OrdinalIgnoreCase)) == true
                ? CaptureCurrentCardSkinPreset(preset.CategoryId!,
                    Config.ActiveCardSkinPresets.GetValueOrDefault(preset.CategoryId!, string.Empty))
                : preset.Clone()).ToList();
        state.Monsters = state.Monsters.Select(preset =>
            Config.MonsterSkinCategoryGroups.ContainsKey(preset.CategoryId)
                ? CaptureCurrentMonsterSkinPreset(preset.CategoryId,
                    Config.ActiveMonsterSkinPresets.GetValueOrDefault(preset.CategoryId, string.Empty))
                : preset.Clone()).ToList();
    }

    internal static void SaveCharacterSkinBundleRunPresets()
    {
        lock (Sync)
        {
            if (_characterSkinBundleRunSnapshot == null || _characterSkinBundleRunSavePath == null) return;
            CaptureCharacterSkinBundleRunPresets();
            CharacterSkinBundleRunStore.Save(_characterSkinBundleRunSavePath, _characterSkinBundleRunState!);
        }
    }

    internal static void BindNewCharacterSkinBundleRun(RunManager manager, RunState state, bool multiplayer)
    {
        lock (Sync)
        {
            var local = state.Players.FirstOrDefault(player => player.NetId == manager.NetService.NetId);
            if (local == null) return;
            var startTime = (long)AccessTools.Field(typeof(RunManager), "_startTime").GetValue(manager)!;
            var identity = CharacterSkinBundleRunState.Identity(startTime, state.Rng.StringSeed,
                local.NetId, state.Players.Select(player => player.NetId));
            var path = GetCharacterSkinBundleRunSavePath(multiplayer);
            // Empty records are intentional: a new run without a bundle must not inherit a
            // previous run's bundle even when the player reuses a seed and the same character.
            var record = _characterSkinBundleRunSnapshot != null ? _characterSkinBundleRunState : null;
            record ??= new CharacterSkinBundleRunState { CharacterGroupId = local.Character.Id.Entry.ToLowerInvariant() };
            record.RunIdentity = identity;
            CharacterSkinBundleRunStore.Save(path, record);
            if (_characterSkinBundleRunSnapshot == null) return;
            _characterSkinBundleRunSavePath = path;
            ModLog.Info($"已绑定本局皮肤包“{record.BundleName}”到对局记录：开始时间={startTime}，多人={multiplayer}。");
        }
    }

    internal static void LoadCharacterSkinBundleRun(RunState state, SerializableRun save, ulong localId, bool multiplayer)
    {
        lock (Sync)
        {
            var local = state.Players.FirstOrDefault(player => player.NetId == localId);
            if (local == null) return;
            var path = GetCharacterSkinBundleRunSavePath(multiplayer);
            var identity = CharacterSkinBundleRunState.Identity(save.StartTime, state.Rng.StringSeed,
                localId, state.Players.Select(player => player.NetId));
            var record = CharacterSkinBundleRunStore.LoadMatching(path, identity);
            if (record == null)
            {
                ModLog.Info("继续对局没有找到匹配的皮肤包记录；不根据选角界面的当前包猜测旧局选择。");
                return;
            }
            if (string.IsNullOrWhiteSpace(record.BundleName)) return;
            if (!record.CharacterGroupId.Equals(local.Character.Id.Entry, StringComparison.OrdinalIgnoreCase))
            {
                ModLog.Warn("皮肤包对局记录的角色与本机玩家不匹配，跳过恢复。");
                return;
            }
            if (ResumeCharacterSkinBundleForRun(record)) _characterSkinBundleRunSavePath = path;
        }
    }

    internal static bool ResumeCharacterSkinBundleForRun(CharacterSkinBundleRunState state)
    {
        lock (Sync)
        {
            var catalog = Catalog;
            if (catalog == null) return false;
            if (_characterSkinBundleRunSnapshot != null) RestoreCharacterSkinBundleAfterRun();
            var original = Config;
            var next = original.CloneForBundleTransaction();
            var cards = state.Cards.Where(p => !string.IsNullOrWhiteSpace(p.CategoryId) &&
                catalog.CardGroups.Any(g => g.Id.Equals(p.CategoryId, StringComparison.OrdinalIgnoreCase)))
                .Select(p => p.Clone()).ToList();
            var monsters = state.Monsters.Where(p => Config.MonsterSkinCategoryGroups.ContainsKey(p.CategoryId))
                .Select(p => p.Clone()).ToList();
            var cardGroups = cards.Select(p => p.CategoryId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var visualGroups = monsters.SelectMany(p => Config.MonsterSkinCategoryGroups[p.CategoryId])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            void Prepare()
            {
                foreach (var preset in cards)
                    ApplyCardPresetSettings(catalog.CardGroups.First(g => g.Id.Equals(preset.CategoryId,
                        StringComparison.OrdinalIgnoreCase)), preset);
                foreach (var preset in monsters) ApplyMonsterPresetSettings(preset);
                foreach (var group in visualGroups) UpdateVisualProviderPriority(group, Config.GetSelection(group));
            }
            void Refresh()
            {
                CardPreviewSelections.Clear();
                foreach (var group in cardGroups) ClearCardPortraitCache(group);
                foreach (var group in visualGroups) ClearRuntimeResourceCache(group);
                var failures = FailureIsolatedActionRunner.Run([
                    ("visuals", () => { if (visualGroups.Count > 0) MountOverlay(visualGroups); }),
                    ("cards", () => { if (cardGroups.Count > 0) MountCardOverlay(cardGroups); })]);
                if (failures.Count > 0) throw new AggregateException(failures.Select(f => f.Exception));
            }
            original.Save(CharacterSkinBundleRunSnapshotPath);
            var error = StagedConfigurationTransaction.Run(original, next, value => Config = value,
                Prepare, Refresh, _ => original.Save(ConfigPath), Refresh);
            LastError = error?.Message;
            if (error != null)
            {
                DeleteCharacterSkinBundleRunSnapshot();
                ModLog.Error("继续对局恢复皮肤包失败，已保留全局预设和原对局记录：" + error);
                return false;
            }
            _characterSkinBundleRunSnapshot = original;
            _characterSkinBundleRunCardGroups = cardGroups;
            _characterSkinBundleRunVisualGroups = visualGroups;
            _characterSkinBundleRunState = new CharacterSkinBundleRunState {
                RunIdentity = state.RunIdentity, CharacterGroupId = state.CharacterGroupId,
                BundleName = state.BundleName, Cards = state.Cards.Select(p => p.Clone()).ToList(),
                Monsters = state.Monsters.Select(p => p.Clone()).ToList() };
            ModLog.Info($"继续对局已恢复皮肤包“{state.BundleName}”：卡牌分类={cards.Count}，怪物地区={monsters.Count}；离开后恢复当前全局预设。");
            return true;
        }
    }
}

[HarmonyPatch]
internal static class CharacterSkinBundleNewRunBindingPatch
{
    private static IEnumerable<MethodBase> TargetMethods() => new[] {
        AccessTools.Method(typeof(RunManager), nameof(RunManager.SetUpNewSingleplayer)),
        AccessTools.Method(typeof(RunManager), nameof(RunManager.SetUpNewMultiplayer)) };
    private static void Postfix(RunManager __instance, RunState state, bool shouldSave, MethodBase __originalMethod)
    {
        if (!shouldSave) return;
        try { SkinService.BindNewCharacterSkinBundleRun(__instance, state, __originalMethod.Name == nameof(RunManager.SetUpNewMultiplayer)); }
        catch (Exception error) { ModLog.Error("保存新对局的皮肤包记录失败，不影响游戏开局：" + error); }
    }
}

[HarmonyPatch]
internal static class CharacterSkinBundleSavedRunPatch
{
    private static IEnumerable<MethodBase> TargetMethods() => new[] {
        AccessTools.Method(typeof(RunManager), nameof(RunManager.SetUpSavedSingleplayer)),
        AccessTools.Method(typeof(RunManager), nameof(RunManager.SetUpSavedMultiplayer)) };
    [HarmonyPriority(Priority.First)]
    private static void Prefix(RunState state, object[] __args)
    {
        try
        {
            if (__args.OfType<SerializableRun>().FirstOrDefault() is { } save)
                SkinService.LoadCharacterSkinBundleRun(state, save, 1, multiplayer: false);
            else if (__args.OfType<LoadRunLobby>().FirstOrDefault() is { } lobby)
                SkinService.LoadCharacterSkinBundleRun(state, lobby.Run, lobby.NetService.NetId, multiplayer: true);
        }
        catch (Exception error) { ModLog.Error("继续对局恢复皮肤包记录失败，不阻止游戏读档：" + error); }
    }
}

[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SaveRun))]
internal static class CharacterSkinBundleRunSavePatch
{
    private static void Prefix()
    {
        try { SkinService.SaveCharacterSkinBundleRunPresets(); }
        catch (Exception error) { ModLog.Error("保存本局皮肤包外观失败，不阻止游戏存档：" + error); }
    }
}
