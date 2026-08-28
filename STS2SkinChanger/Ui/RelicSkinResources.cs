using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal static class RelicSkinResources
{
    private static readonly HashSet<string> ReportedFailures =
        new(StringComparer.OrdinalIgnoreCase);

    public static void Replace(string resourcePath, ref Texture2D result)
    {
        try
        {
            result = SkinService.GetSelectedRelicIcon(resourcePath) ?? result;
        }
        catch (Exception exception)
        {
            if (ReportedFailures.Add(resourcePath))
            {
                ModLog.Warn(
                    $"隔离加载皮肤附带的遗物图标失败，已保留游戏图标 {resourcePath}：" +
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
