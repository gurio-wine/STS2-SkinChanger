using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using STS2SkinChanger.Catalog;
using System.Reflection;

namespace STS2SkinChanger.Core;

internal static class FrameworkSkinRuntime
{
    public static bool TryGetCharacterResource(
        CharacterModel model,
        string propertyName,
        out string path)
    {
        path = string.Empty;
        return TryGetCharacterContract(model, out var contract) &&
               contract.CharacterResources.TryGetValue(propertyName, out path!);
    }

    public static bool TryGetPoolResource(
        CardPoolModel pool,
        string propertyName,
        out string path)
    {
        path = string.Empty;
        var groupId = NormalizeToken(pool.Title);
        return SkinService.TryGetSelectedFrameworkContract(groupId, out var contract) &&
               contract.CharacterResources.TryGetValue(propertyName, out path!);
    }

    public static bool TryGetCharacterColor(
        CharacterModel model,
        string propertyName,
        out Color color)
    {
        color = default;
        return TryGetCharacterContract(model, out var contract) &&
               contract.CharacterValues.TryGetValue(propertyName, out var value) &&
               TryParseColor(value, out color);
    }

    public static bool TryGetPoolColor(
        CardPoolModel pool,
        string propertyName,
        out Color color)
    {
        color = default;
        var groupId = NormalizeToken(pool.Title);
        return SkinService.TryGetSelectedFrameworkContract(groupId, out var contract) &&
               contract.CharacterValues.TryGetValue(propertyName, out var value) &&
               TryParseColor(value, out color);
    }

    public static bool TryGetEnergyLayers(
        CharacterModel model,
        out IReadOnlyList<string> paths)
    {
        paths = [];
        if (!TryGetCharacterContract(model, out var contract) ||
            !contract.CharacterResourceLists.TryGetValue("EnergyLayers", out var selectedPaths))
        {
            return false;
        }

        paths = selectedPaths;
        return true;
    }

    public static bool TryGetOrbResource(
        OrbModel model,
        string propertyName,
        out string path)
    {
        path = string.Empty;
        var targetName = model.GetType().Name;
        var descriptor = SkinService.GetSelectedFrameworkContracts()
            .SelectMany(contract => contract.Orbs)
            .LastOrDefault(candidate => candidate.TargetModelName.Equals(
                targetName,
                StringComparison.Ordinal));
        return descriptor != null && descriptor.Resources.TryGetValue(propertyName, out path!);
    }

    public static bool TryGetOrbColor(
        OrbModel model,
        string propertyName,
        out Color color)
    {
        color = default;
        var targetName = model.GetType().Name;
        var descriptor = SkinService.GetSelectedFrameworkContracts()
            .SelectMany(contract => contract.Orbs)
            .LastOrDefault(candidate => candidate.TargetModelName.Equals(
                targetName,
                StringComparison.Ordinal));
        return descriptor != null &&
               descriptor.Values.TryGetValue(propertyName, out var value) &&
               TryParseColor(value, out color);
    }

    public static bool TryGetRelicResource(
        RelicModel model,
        string propertyName,
        out string path)
    {
        path = string.Empty;
        var targetName = model.GetType().Name;
        var descriptor = SkinService.GetSelectedFrameworkContracts()
            .SelectMany(contract => contract.Relics)
            .LastOrDefault(candidate => candidate.TargetModelName.Equals(
                targetName,
                StringComparison.Ordinal));
        return descriptor != null && descriptor.Resources.TryGetValue(propertyName, out path!);
    }

    public static string NormalizeToken(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool TryGetCharacterContract(
        CharacterModel model,
        out FrameworkCharacterSkinContract contract) =>
        SkinService.TryGetSelectedFrameworkContract(
            NormalizeToken(model.Id.Entry),
            out contract);

    private static bool TryParseColor(string value, out Color color)
    {
        try
        {
            color = new Color(value);
            return true;
        }
        catch
        {
            color = default;
            return false;
        }
    }
}

[HarmonyPatch(typeof(CharacterModel), "get_TrailPath")]
internal static class FrameworkCharacterTrailPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    private static bool Prefix(CharacterModel __instance, ref string __result)
    {
        if (!FrameworkSkinRuntime.TryGetCharacterResource(__instance, "CardTrail", out var path))
        {
            return true;
        }

        __result = path;
        return false;
    }
}

[HarmonyPatch(typeof(CardPoolModel), "get_FrameMaterialPath")]
internal static class FrameworkCardFramePatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    private static bool Prefix(CardPoolModel __instance, ref string __result)
    {
        if (!FrameworkSkinRuntime.TryGetPoolResource(
                __instance,
                "CardFrameMaterial",
                out var path))
        {
            return true;
        }

        __result = path;
        return false;
    }
}

[HarmonyPatch(typeof(CardPoolModel), "get_EnergyIconPath")]
internal static class FrameworkCardEnergyIconPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    private static bool Prefix(CardPoolModel __instance, ref string __result)
    {
        if (!FrameworkSkinRuntime.TryGetPoolResource(__instance, "EnergyIcon", out var path))
        {
            return true;
        }

        __result = path;
        return false;
    }
}

[HarmonyPatch(typeof(EnergyIconHelper), nameof(EnergyIconHelper.GetPath), [typeof(string)])]
internal static class FrameworkEnergyIconHelperPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    private static bool Prefix(string prefix, ref string __result)
    {
        var groupId = FrameworkSkinRuntime.NormalizeToken(prefix);
        if (!SkinService.TryGetSelectedFrameworkContract(groupId, out var contract) ||
            !contract.CharacterResources.TryGetValue("EnergyIcon", out var path))
        {
            return true;
        }

        __result = path;
        return false;
    }
}

[HarmonyPatch]
internal static class FrameworkCharacterEnergyColorPatch
{
    private static IEnumerable<MethodBase> TargetMethods() =>
        AccessTools.AllTypes()
            .Where(type => !type.IsAbstract && typeof(CharacterModel).IsAssignableFrom(type))
            .Select(type => AccessTools.PropertyGetter(type, "EnergyLabelOutlineColor"))
            .Where(method => method != null)
            .Cast<MethodBase>()
            .Distinct();

    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    private static bool Prefix(CharacterModel __instance, ref Color __result)
    {
        if (!FrameworkSkinRuntime.TryGetCharacterColor(
                __instance,
                "EnergyLabelColor",
                out var color))
        {
            return true;
        }

        __result = color;
        return false;
    }
}

[HarmonyPatch]
internal static class FrameworkCardEnergyColorPatch
{
    private static IEnumerable<MethodBase> TargetMethods() =>
        AccessTools.AllTypes()
            .Where(type => !type.IsAbstract && typeof(CardPoolModel).IsAssignableFrom(type))
            .Select(type => AccessTools.PropertyGetter(type, "EnergyOutlineColor"))
            .Where(method => method != null)
            .Cast<MethodBase>()
            .Distinct();

    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    private static bool Prefix(CardPoolModel __instance, ref Color __result)
    {
        if (!FrameworkSkinRuntime.TryGetPoolColor(
                __instance,
                "EnergyLabelOutlineColor",
                out var color))
        {
            return true;
        }

        __result = color;
        return false;
    }
}

[HarmonyPatch(typeof(NEnergyCounter), "_Ready")]
internal static class FrameworkEnergyLayersPatch
{
    private static readonly FieldInfo? PlayerField = AccessTools.Field(typeof(NEnergyCounter), "_player");
    private static readonly string[] LayerPaths =
    [
        "Layers/Layer1",
        "Layers/RotationLayers/Layer2",
        "Layers/RotationLayers/Layer3",
        "Layers/Layer4",
        "Layers/Layer5"
    ];

    [HarmonyPostfix]
    private static void Postfix(NEnergyCounter __instance)
    {
        if (PlayerField?.GetValue(__instance) is not Player player ||
            !FrameworkSkinRuntime.TryGetEnergyLayers(player.Character, out var paths))
        {
            return;
        }

        for (var index = 0; index < Math.Min(paths.Count, LayerPaths.Length); index++)
        {
            var layer = __instance.GetNodeOrNull<TextureRect>(LayerPaths[index]);
            if (layer != null)
            {
                layer.Texture = ResourceLoader.Load<Texture2D>(
                    paths[index],
                    null,
                    ResourceLoader.CacheMode.Reuse);
            }
        }
    }
}

[HarmonyPatch(typeof(OrbModel), "get_IconPath")]
internal static class FrameworkOrbIconPatch
{
    [HarmonyPrefix]
    private static bool Prefix(OrbModel __instance, ref string __result) =>
        Apply(__instance, ref __result, "CustomIconPath");

    private static bool Apply(OrbModel model, ref string result, string propertyName)
    {
        if (!FrameworkSkinRuntime.TryGetOrbResource(model, propertyName, out var path))
        {
            return true;
        }
        result = path;
        return false;
    }
}

[HarmonyPatch(typeof(OrbModel), "get_SpritePath")]
internal static class FrameworkOrbSpritePatch
{
    [HarmonyPrefix]
    private static bool Prefix(OrbModel __instance, ref string __result)
    {
        if (!FrameworkSkinRuntime.TryGetOrbResource(
                __instance,
                "CustomSpritePath",
                out var path))
        {
            return true;
        }
        __result = path;
        return false;
    }
}

[HarmonyPatch]
internal static class FrameworkOrbDarkenedColorPatch
{
    private static IEnumerable<MethodBase> TargetMethods() =>
        AccessTools.AllTypes()
            .Where(type => !type.IsAbstract && typeof(OrbModel).IsAssignableFrom(type))
            .Select(type => AccessTools.DeclaredPropertyGetter(type, "DarkenedColor"))
            .Where(method => method != null)
            .Cast<MethodBase>();

    [HarmonyPrefix]
    private static bool Prefix(OrbModel __instance, ref Color __result)
    {
        if (!FrameworkSkinRuntime.TryGetOrbColor(
                __instance,
                "CustomDarkenedColor",
                out var color))
        {
            return true;
        }
        __result = color;
        return false;
    }
}

[HarmonyPatch]
internal static class FrameworkRelicPackedIconPatch
{
    private static IEnumerable<MethodBase> TargetMethods() =>
        RelicGetters("PackedIconPath");

    [HarmonyPrefix]
    private static bool Prefix(RelicModel __instance, ref string __result) =>
        Apply(__instance, ref __result, "PackedIconPath");

    internal static IEnumerable<MethodBase> RelicGetters(string propertyName) =>
        AccessTools.AllTypes()
            .Where(type => !type.IsAbstract && typeof(RelicModel).IsAssignableFrom(type))
            .Select(type => AccessTools.PropertyGetter(type, propertyName))
            .Where(method => method != null)
            .Cast<MethodBase>()
            .Distinct();

    internal static bool Apply(
        RelicModel model,
        ref string result,
        string propertyName)
    {
        if (!FrameworkSkinRuntime.TryGetRelicResource(model, propertyName, out var path))
        {
            return true;
        }
        result = path;
        return false;
    }
}

[HarmonyPatch]
internal static class FrameworkRelicOutlinePatch
{
    private static IEnumerable<MethodBase> TargetMethods() =>
        FrameworkRelicPackedIconPatch.RelicGetters("PackedIconOutlinePath");

    [HarmonyPrefix]
    private static bool Prefix(RelicModel __instance, ref string __result) =>
        FrameworkRelicPackedIconPatch.Apply(
            __instance,
            ref __result,
            "PackedIconOutlinePath");
}

[HarmonyPatch]
internal static class FrameworkRelicBigIconPatch
{
    private static IEnumerable<MethodBase> TargetMethods() =>
        FrameworkRelicPackedIconPatch.RelicGetters("BigIconPath")
            .Concat(FrameworkRelicPackedIconPatch.RelicGetters("ResolvedBigIconPath"))
            .Distinct();

    [HarmonyPrefix]
    private static bool Prefix(RelicModel __instance, ref string __result) =>
        FrameworkRelicPackedIconPatch.Apply(__instance, ref __result, "BigIconPath");
}
