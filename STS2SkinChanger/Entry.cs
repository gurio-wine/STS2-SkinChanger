using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Modding;
using STS2SkinChanger.Core;

namespace STS2SkinChanger;

[ModInitializer("Initialize")]
public static class Entry
{
    public const string ModId = "Gurio.SkinChanger";
    public const string LegacyModId = "STS2SkinChanger";

    public static bool IsSelfModId(string? modId) =>
        modId != null &&
        (modId.Equals(ModId, StringComparison.OrdinalIgnoreCase) ||
         modId.Equals(LegacyModId, StringComparison.OrdinalIgnoreCase));

    public static void Initialize()
    {
        ManagedSkinModLoader.Initialize();
        var harmony = new Harmony(ModId);
        try
        {
            harmony.PatchAll();
            ModLog.Info("代码补丁已加载。等待游戏资源初始化。");
        }
        catch (Exception exception)
        {
            // PatchAll 不是事务操作；失败时撤掉已安装的同 ID 补丁，避免半初始化状态。
            try
            {
                harmony.UnpatchAll(ModId);
            }
            catch (Exception rollbackException)
            {
                ModLog.Error("回滚已安装补丁失败：" + rollbackException.GetBaseException().Message);
            }

            ModLog.Error(
                "安装代码补丁失败，本 Mod 已停用本次会话的代码接管：" +
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
        SkinService.PrepareSelectedCharacterPreviews();
        ExternalCardVisualBridge.WarmUp();
    }
}

[HarmonyPatch(typeof(ModManager), nameof(ModManager.GetModdedLocTables))]
internal static class CosmeticLocalizationOwnershipPatch
{
    // PCK namespaces remain mounted after a skin is deselected (and can also be mounted for
    // card art alone). Filter the game's merge inputs rather than trying to unload those files.
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(ref IEnumerable<string> __result) =>
        __result = SkinService.FilterModdedLocalizationTables(__result);
}
