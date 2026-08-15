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
        var harmony = new Harmony(ModId);
        harmony.PatchAll();
        ModLog.Info("代码补丁已加载。等待游戏资源初始化。");
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
