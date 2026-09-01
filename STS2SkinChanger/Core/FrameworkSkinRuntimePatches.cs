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

    public static FrameworkRelicVisualPlan? ResolveRelicVisual(
        RelicModel model,
        bool largeIcon)
    {
        var contracts = SkinService.GetSelectedFrameworkContracts();
        var plan = FrameworkRelicVisualPolicy.Resolve(
            contracts.SelectMany(contract => contract.Relics),
            model.GetType().Name,
            largeIcon);
        FrameworkRelicDiagnostics.ReportResolution(model, largeIcon, contracts, plan);
        return plan;
    }

    public static bool HasSelectedCharacterContract(CharacterModel model) =>
        TryGetCharacterContract(model, out _);

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

internal static class FrameworkRelicDiagnostics
{
    private static readonly HashSet<string> ReportedResolutions = new(StringComparer.Ordinal);
    private static readonly object Sync = new();

    public static void ReportPatchInstallation()
    {
        ReportPatch("PackedIconPath", AccessTools.PropertyGetter(typeof(RelicModel), "PackedIconPath"));
        ReportPatch("Icon", AccessTools.PropertyGetter(typeof(RelicModel), nameof(RelicModel.Icon)));
        ReportPatch("IconOutline", AccessTools.PropertyGetter(typeof(RelicModel), nameof(RelicModel.IconOutline)));
        ReportPatch("BigIcon", AccessTools.PropertyGetter(typeof(RelicModel), nameof(RelicModel.BigIcon)));
    }

    public static void ReportResolution(
        RelicModel model,
        bool largeIcon,
        IReadOnlyList<FrameworkCharacterSkinContract> contracts,
        FrameworkRelicVisualPlan? plan)
    {
        var key = model.GetType().FullName + "|" + largeIcon;
        lock (Sync)
        {
            if (!ReportedResolutions.Add(key))
            {
                return;
            }
        }

        var relicTargets = contracts
            .SelectMany(contract => contract.Relics)
            .Select(relic => relic.TargetModelName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var path = plan?.IconPath ?? "<none>";
        var exists = plan != null && ResourceLoader.Exists(plan.IconPath);
        ModLog.Info(
            $"框架遗物诊断：运行类型={model.GetType().FullName}，大图={largeIcon}，" +
            $"选中契约={contracts.Count}，遗物目标=[{string.Join(",", relicTargets)}]，" +
            $"解析路径={path}，资源存在={exists}。");
    }

    private static void ReportPatch(string label, MethodBase? target)
    {
        if (target == null)
        {
            ModLog.Warn($"框架遗物诊断：未找到 {label} getter。");
            return;
        }

        var patches = Harmony.GetPatchInfo(target);
        var ownedPrefixes = patches?.Prefixes.Count(patch =>
            patch.owner.Equals(Entry.ModId, StringComparison.Ordinal)) ?? 0;
        var ownedPostfixes = patches?.Postfixes.Count(patch =>
            patch.owner.Equals(Entry.ModId, StringComparison.Ordinal)) ?? 0;
        ModLog.Info(
            $"框架遗物诊断：{target.DeclaringType?.FullName}.{target.Name}，" +
            $"本 Mod 前置={ownedPrefixes}，后置={ownedPostfixes}。");
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
