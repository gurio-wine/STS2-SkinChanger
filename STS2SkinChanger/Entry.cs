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
}

[HarmonyPatch(typeof(OneTimeInitialization), nameof(OneTimeInitialization.ExecuteEssential))]
internal static class EssentialInitializationPatch
{
    private static void Prefix()
    {
        Entry.PatchAncientWaifusRuntime(new Harmony(Entry.ModId));
        SkinService.InitializeBeforeAssets();
    }
}
