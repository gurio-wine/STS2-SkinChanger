using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Modding;
using STS2SkinChanger.Core;

namespace STS2SkinChanger;

[ModInitializer("Initialize")]
public static class Entry
{
    public const string ModId = "STS2SkinChanger";

    public static void Initialize()
    {
        var harmony = new Harmony(ModId);
        harmony.PatchAll();
        PatchAncientWaifusRuntime(harmony);
        ModLog.Info("代码补丁已加载。等待游戏资源初始化。");
    }

    internal static void PatchAncientWaifusRuntime(Harmony harmony)
    {
        var prefix = new HarmonyMethod(AccessTools.Method(typeof(Entry), nameof(SkipConflictingSkinRuntime)));
        var target = AccessTools.Method("AncientWaifus.Core.GlobalTouchHook:RegisterHook");
        if (target == null || Harmony.GetPatchInfo(target)?.Prefixes.Any(patch => patch.owner == ModId) == true)
        {
            return;
        }

        try
        {
            harmony.Patch(target, prefix: prefix);
            ModLog.Info("已接管 AncientWaifus 的路径切换并禁用其过期输入钩子。");
        }
        catch (Exception exception)
        {
            ModLog.Warn($"无法停用冲突的皮肤运行时 {target.DeclaringType?.FullName}.{target.Name}：{exception.Message}");
        }
    }

    private static bool SkipConflictingSkinRuntime() => false;

    internal static void PatchCardPortraitProviders(Harmony harmony)
    {
        var optionIds = SkinService.Catalog?.CardGroups
            .SelectMany(group => group.Options)
            .Select(option => option.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (optionIds == null || optionIds.Count == 0)
        {
            return;
        }

        var removed = 0;
        foreach (var target in new[]
                 {
                     AccessTools.PropertyGetter(
                         typeof(MegaCrit.Sts2.Core.Models.CardModel),
                         nameof(MegaCrit.Sts2.Core.Models.CardModel.Portrait)),
                     AccessTools.PropertyGetter(
                         typeof(MegaCrit.Sts2.Core.Models.CardModel),
                         nameof(MegaCrit.Sts2.Core.Models.CardModel.PortraitPath))
                 })
        {
            var owners = Harmony.GetPatchInfo(target)?.Postfixes
                .Select(patch => patch.owner)
                .Where(optionIds.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? [];
            foreach (var owner in owners)
            {
                try
                {
                    removed += Harmony.GetPatchInfo(target)?.Postfixes.Count(patch =>
                        patch.owner.Equals(owner, StringComparison.OrdinalIgnoreCase)) ?? 0;
                    harmony.Unpatch(target, HarmonyPatchType.Postfix, owner);
                }
                catch (Exception exception)
                {
                    ModLog.Warn(
                        $"无法移除冲突的卡图运行时 {owner}/{target.Name}：{exception.Message}");
                }
            }
        }

        if (removed > 0)
        {
            ModLog.Info($"已接管 {removed} 个卡图路径/贴图全局补丁，改为按所属卡池应用。");
        }
    }
}

[HarmonyPatch(typeof(OneTimeInitialization), nameof(OneTimeInitialization.ExecuteEssential))]
internal static class EssentialInitializationPatch
{
    private static void Prefix()
    {
        Entry.PatchAncientWaifusRuntime(new Harmony(Entry.ModId));
        SkinService.InitializeBeforeAssets();
        Entry.PatchCardPortraitProviders(new Harmony(Entry.ModId));
    }

    private static void Postfix()
    {
        SkinService.InitializeCardGroupsAfterModels();
        Entry.PatchCardPortraitProviders(new Harmony(Entry.ModId));
    }
}
