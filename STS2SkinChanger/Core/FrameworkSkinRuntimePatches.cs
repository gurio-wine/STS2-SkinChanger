using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Animation;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using STS2SkinChanger.Catalog;
using System.Reflection;

namespace STS2SkinChanger.Core;

internal static class FrameworkSkinRuntime
{
    public static bool TryGetCharacterContract(
        CharacterModel model,
        out string groupId,
        out FrameworkCharacterSkinContract contract)
    {
        groupId = NormalizeToken(model.Id.Entry);
        return SkinService.TryGetSelectedFrameworkContract(groupId, out contract) && UsesDeclarativePresentation(contract);
    }

    internal static bool UsesDeclarativePresentation(FrameworkCharacterSkinContract contract) =>
        !FrameworkRegistryCooperation.UsesNativePresentation(contract);

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
               UsesDeclarativePresentation(contract) &&
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
               UsesDeclarativePresentation(contract) &&
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
            .Where(UsesDeclarativePresentation)
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
            .Where(UsesDeclarativePresentation)
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
            .Where(UsesDeclarativePresentation)
            .SelectMany(contract => contract.Relics)
            .LastOrDefault(candidate => candidate.TargetModelName.Equals(
                targetName,
                StringComparison.Ordinal));
        return descriptor != null && descriptor.Resources.TryGetValue(propertyName, out path!);
    }

    public static FrameworkRelicVisualPlan? ResolveRelicVisual(
        RelicModel model,
        bool largeIcon) =>
        FrameworkRelicVisualPolicy.Resolve(
            SkinService.GetSelectedFrameworkContracts()
                .Where(UsesDeclarativePresentation)
                .SelectMany(contract => contract.Relics),
            model.GetType().Name,
            largeIcon);

    public static bool HasSelectedCharacterContract(CharacterModel model) =>
        TryGetCharacterContract(model, out _);

    public static string NormalizeToken(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool TryGetCharacterContract(
        CharacterModel model,
        out FrameworkCharacterSkinContract contract) =>
        TryGetCharacterContract(model, out _, out contract);

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

/// <summary>
/// The thunninoi/BaseLib skin contract deliberately allows CombatVisual to point at a plain
/// Node2D scene. Its original manager converts that scene through
/// NodeFactory&lt;NCreatureVisuals&gt; before returning it to the game. A global resource redirect
/// alone cannot preserve that contract because CharacterModel.CreateVisuals instantiates the
/// scene directly as NCreatureVisuals and throws before a postfix can repair it.
/// </summary>
[HarmonyPatch(typeof(CharacterModel), nameof(CharacterModel.CreateVisuals))]
internal static class FrameworkCombatVisualPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(CharacterModel __instance, ref NCreatureVisuals __result)
    {
        if (!FrameworkSkinRuntime.TryGetCharacterContract(
                __instance,
                out var groupId,
                out var contract) ||
            !contract.CharacterResources.ContainsKey("CombatVisual"))
        {
            return true;
        }

        var canonicalPath =
            $"res://scenes/creature_visuals/{__instance.Id.Entry.ToLowerInvariant()}.tscn";
        __result = SkinService.InstantiateManagedCharacterCreatureVisuals(groupId, canonicalPath);
        return false;
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
            !FrameworkSkinRuntime.UsesDeclarativePresentation(contract) ||
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
        new[] { AccessTools.PropertyGetter(typeof(RelicModel), propertyName) }
            .Concat(AccessTools.AllTypes()
                .Where(type => !type.IsAbstract && typeof(RelicModel).IsAssignableFrom(type))
                .Select(type => AccessTools.DeclaredPropertyGetter(type, propertyName)))
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

[HarmonyPatch(typeof(RelicModel), "get_Icon")]
internal static class FrameworkRelicIconTexturePatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    private static bool Prefix(RelicModel __instance, ref Texture2D __result)
    {
        var plan = FrameworkSkinRuntime.ResolveRelicVisual(__instance, largeIcon: false);
        if (plan == null)
        {
            return true;
        }

        return FrameworkRelicTextureLoader.Apply(plan.IconPath, ref __result);
    }
}

[HarmonyPatch(typeof(RelicModel), "get_IconOutline")]
internal static class FrameworkRelicOutlineTexturePatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    private static bool Prefix(RelicModel __instance, ref Texture2D __result)
    {
        var plan = FrameworkSkinRuntime.ResolveRelicVisual(__instance, largeIcon: false);
        if (plan?.OutlinePath == null)
        {
            return true;
        }

        return FrameworkRelicTextureLoader.Apply(plan.OutlinePath, ref __result);
    }
}

[HarmonyPatch(typeof(RelicModel), "get_BigIcon")]
internal static class FrameworkRelicBigIconTexturePatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    private static bool Prefix(RelicModel __instance, ref Texture2D __result)
    {
        var plan = FrameworkSkinRuntime.ResolveRelicVisual(__instance, largeIcon: true);
        if (plan == null)
        {
            return true;
        }

        // RelicModel caches ResolvedBigIconPath after its first access. Returning the selected
        // texture from the public BigIcon boundary avoids both the stale vanilla cache and
        // contaminating that cache when the player later switches back to another skin.
        return FrameworkRelicTextureLoader.Apply(plan.IconPath, ref __result);
    }
}

internal static class FrameworkRelicTextureLoader
{
    public static bool Apply(string path, ref Texture2D result)
    {
        var texture = ResourceLoader.Load<Texture2D>(
            path,
            null,
            ResourceLoader.CacheMode.Reuse);
        if (texture == null)
        {
            ModLog.Warn($"无法加载框架遗物图片：{path}");
            return true;
        }

        result = texture;
        return false;
    }
}

[HarmonyPatch]
internal static class FrameworkEntryAnimationPatch
{
    private static readonly FieldInfo? CurrentStateField =
        AccessTools.Field(typeof(CreatureAnimator), "_currentState");
    private static readonly MethodInfo? SetAnimationMethod =
        ResolveAnimationMethod("SetAnimation", 3);
    private static readonly MethodInfo? AddAnimationMethod =
        ResolveAnimationMethod("AddAnimation", 4);

    private static IEnumerable<MethodBase> TargetMethods() =>
        AccessTools.AllTypes()
            .Where(type => !type.IsAbstract && typeof(CharacterModel).IsAssignableFrom(type))
            .Select(type => AccessTools.Method(type, "GenerateAnimator"))
            .Where(method => method != null)
            .Cast<MethodBase>()
            .Distinct();

    [HarmonyPostfix]
    [HarmonyPriority(Priority.High)]
    private static void Postfix(
        CharacterModel __instance,
        MegaSprite __0,
        CreatureAnimator __result)
    {
        try
        {
            if (CurrentStateField?.GetValue(__result) is not AnimState currentState)
            {
                return;
            }

            var plan = FrameworkEntryAnimationPolicy.Resolve(
                FrameworkSkinRuntime.HasSelectedCharacterContract(__instance),
                __0.HasAnimation("entry"),
                currentState.Id,
                currentState.IsLooping);
            if (plan == null || SetAnimationMethod == null || AddAnimationMethod == null)
            {
                return;
            }

            var animationState = __0.GetAnimationState();
            SetAnimationMethod.Invoke(
                animationState,
                [plan.EntryAnimationId, false, 0]);
            AddAnimationMethod.Invoke(
                animationState,
                [plan.QueuedAnimationId, 0f, plan.QueuedAnimationLoops, 0]);

            var entryState = new AnimState(plan.EntryAnimationId)
            {
                NextState = currentState
            };
            __result.AddAnyState("Entry", entryState);
            CurrentStateField.SetValue(__result, entryState);
        }
        catch (Exception exception)
        {
            ModLog.Warn(
                "应用框架皮肤登场动画失败：" +
                exception.GetBaseException().Message);
        }
    }

    private static MethodInfo? ResolveAnimationMethod(string name, int parameterCount) =>
        typeof(MegaAnimationState)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(method =>
            {
                if (!method.Name.Equals(name, StringComparison.Ordinal) ||
                    method.GetParameters() is not { } parameters ||
                    parameters.Length != parameterCount)
                {
                    return false;
                }

                var expected = name.Equals("SetAnimation", StringComparison.Ordinal)
                    ? new[] { typeof(string), typeof(bool), typeof(int) }
                    : new[] { typeof(string), typeof(float), typeof(bool), typeof(int) };
                return parameters.Select(parameter => parameter.ParameterType).SequenceEqual(expected);
            });
}
