using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal static class RelicSkinResources
{
    private static readonly HashSet<string> ReportedFailures =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ReportedStandaloneOverrides =
        new(StringComparer.OrdinalIgnoreCase);

    public static void Replace(string resourcePath, ref Texture2D result)
    {
        try
        {
            // A selected character's retained icon callback can return an explicit standalone
            // texture instead of an atlas slice. Do not overwrite that authored result with our
            // vanilla atlas repair. Unselected files and shared atlases still use normal isolation.
            if (SkinService.IsSelectedCharacterStandaloneTexture(result.ResourcePath))
            {
                if (ReportedStandaloneOverrides.Add(resourcePath + "\n" + result.ResourcePath))
                    ModLog.Info($"已保留选中角色皮肤的独立遗物图标：{resourcePath} -> {result.ResourcePath}");
                return;
            }
            result = SkinService.GetRelicIconOverride(resourcePath) ?? result;
        }
        catch (Exception exception)
        {
            if (ReportedFailures.Add(resourcePath))
            {
                ModLog.Warn(
                    $"隔离加载遗物图标失败，已保留当前图标 {resourcePath}：" +
                    exception.GetBaseException().Message);
            }
        }
    }
}

[HarmonyPatch(typeof(RelicModel), nameof(RelicModel.Icon), MethodType.Getter)]
internal static class RelicIconSkinPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(RelicModel __instance, ref Texture2D __result) =>
        RelicSkinResources.Replace(__instance.PackedIconPath, ref __result);
}

[HarmonyPatch(typeof(RelicModel), nameof(RelicModel.IconOutline), MethodType.Getter)]
internal static class RelicIconOutlineSkinPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(RelicModel __instance, ref Texture2D __result)
    {
        var normalPath = __instance.PackedIconPath;
        var outlinePath = normalPath.Replace(
            "/relic_atlas.sprites/",
            "/relic_outline_atlas.sprites/",
            StringComparison.OrdinalIgnoreCase);
        if (!outlinePath.Equals(normalPath, StringComparison.OrdinalIgnoreCase))
        {
            RelicSkinResources.Replace(outlinePath, ref __result);
        }
    }
}
