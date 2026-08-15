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
        ManagedSkinModLoader.Initialize();
        try
        {
            var harmony = new Harmony(ModId);
            harmony.PatchAll();
            ModLog.Info("代码补丁已加载。等待游戏资源初始化。");
        }
        catch (Exception exception)
        {
            // 游戏更新导致补丁目标缺失时降级运行：皮肤切换继续可用，托管加载与界面注入失效。
            ModLog.Error(
                "安装代码补丁失败，本 Mod 将以受限模式运行：" +
                exception.GetBaseException().Message);
        }
    }

}

[HarmonyPatch(typeof(OneTimeInitialization), nameof(OneTimeInitialization.ExecuteEssential))]
internal static class EssentialInitializationPatch
{
    private static void Prefix()
    {
        VisualPatchGuard.RemoveProviderVisualPatches(ManagedSkinModLoader.ProviderRoots);
        SkinService.InitializeBeforeAssets();
    }

    private static void Postfix()
    {
        VisualPatchGuard.RemoveProviderVisualPatches(ManagedSkinModLoader.ProviderRoots);
        SkinService.InitializeCardGroupsAfterModels();
    }
}
