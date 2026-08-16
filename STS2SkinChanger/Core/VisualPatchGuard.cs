using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using System.Reflection;

namespace STS2SkinChanger.Core;

internal static class VisualPatchGuard
{
    private static readonly Harmony Harmony = new(Entry.ModId);
    private static readonly object CardPatchSync = new();
    private static readonly List<RemovedCardPatch> RemovedCardPatches = [];
    private static readonly List<RemovedProviderPatch> RemovedProviderPatches = [];
    private static readonly Dictionary<string, RemovedProviderPatch> AppliedProviderPatches =
        new(StringComparer.Ordinal);
    private static readonly HashSet<string> ReplayWarnings = new(StringComparer.Ordinal);
    private static readonly string[] VisualNameTokens =
    [
        "Visual", "Scene", "Portrait", "Icon", "Texture", "Sprite", "Atlas",
        "Background", "MapMarker", "Animation", "Sfx", "Sound", "Audio", "Voice"
    ];
    private static readonly string[] VisualTypeNameTokens =
    [
        "Visual", "Vfx", "Spine", "Sprite", "Portrait", "Icon", "Character",
        "Creature", "Merchant", "Card", "Animation", "Audio", "Sound"
    ];

    public static int RemoveProviderVisualPatches(IEnumerable<string> providerRoots)
    {
        var roots = providerRoots
            .Select(NormalizeRoot)
            .Where(root => root != null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (roots.Length == 0)
        {
            return 0;
        }

        var removed = 0;
        var capturedCardPatches = 0;
        var affectedTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in HarmonyLib.Harmony.GetAllPatchedMethods()
                     .Where(IsVisualTarget)
                     .ToArray())
        {
            var patchInfo = HarmonyLib.Harmony.GetPatchInfo(target);
            if (patchInfo == null)
            {
                continue;
            }

            var providerPatches = EnumeratePatches(patchInfo)
                .Select(entry => (entry.Patch, entry.Kind, Root: GetProviderRoot(entry.Patch, roots)))
                .Where(entry => entry.Root != null)
                .DistinctBy(entry => (entry.Patch.PatchMethod, entry.Kind))
                .ToArray();
            foreach (var entry in providerPatches)
            {
                // 已重新挂载（选中生效）的补丁保持原状，不在重复扫描中被移除。
                lock (CardPatchSync)
                {
                    if (AppliedProviderPatches.ContainsKey(ActivePatchKey(target, entry.Patch.PatchMethod)))
                    {
                        continue;
                    }
                }

                try
                {
                    if (typeof(NCard).IsAssignableFrom(target.DeclaringType) &&
                        entry.Kind == ProviderPatchKind.Postfix)
                    {
                        if (RememberCardPatch(entry.Root!, target, entry.Patch.PatchMethod))
                        {
                            capturedCardPatches++;
                        }
                    }

                    Harmony.Unpatch(target, entry.Patch.PatchMethod);
                    removed++;
                    affectedTargets.Add($"{target.DeclaringType?.Name}.{target.Name}");
                    RememberRemovedProviderPatch(entry.Root!, target, entry.Patch, entry.Kind);
                }
                catch (Exception exception)
                {
                    ModLog.Warn(
                        $"无法移除视觉补丁 {entry.Patch.owner}/" +
                        $"{entry.Patch.PatchMethod.DeclaringType?.FullName}.{entry.Patch.PatchMethod.Name}：" +
                        exception.Message);
                }
            }
        }

        if (removed > 0)
        {
            var reapplyable = RemovedProviderPatches.Count(CanReapply);
            ModLog.Info(
                $"已移除 {removed} 个提供者皮肤呈现补丁，保留其余 DLL 功能；目标：" +
                string.Join("、", affectedTargets.OrderBy(name => name)) +
                $"；登记卡牌呈现重放={capturedCardPatches}，" +
                $"选中时可重新挂载={reapplyable}");
        }

        return removed;
    }

    /// <summary>按当前皮肤选择重新挂载/卸载提供者的附加功能补丁（语音、战斗动画等）。</summary>
    public static void SetActiveProviderPatches(IEnumerable<string> providerIds)
    {
        var desired = providerIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (CardPatchSync)
        {
            foreach (var key in AppliedProviderPatches.Keys
                         .Where(key => !desired.Contains(AppliedProviderPatches[key].ProviderId))
                         .ToArray())
            {
                var patch = AppliedProviderPatches[key];
                try
                {
                    Harmony.Unpatch(patch.Target, patch.PatchMethod);
                }
                catch (Exception exception)
                {
                    ModLog.Warn(
                        $"卸载提供者 {patch.ProviderId} 的呈现补丁失败：" +
                        exception.GetBaseException().Message);
                }

                AppliedProviderPatches.Remove(key);
            }

            var activated = 0;
            foreach (var patch in RemovedProviderPatches
                         .Where(patch => desired.Contains(patch.ProviderId) && CanReapply(patch)))
            {
                var key = ActivePatchKey(patch.Target, patch.PatchMethod);
                if (AppliedProviderPatches.ContainsKey(key))
                {
                    continue;
                }

                try
                {
                    var method = new HarmonyMethod(patch.PatchMethod)
                    {
                        priority = patch.Priority,
                        before = patch.Before,
                        after = patch.After
                    };
                    if (patch.Kind == ProviderPatchKind.Prefix)
                    {
                        Harmony.Patch(patch.Target, prefix: method);
                    }
                    else
                    {
                        Harmony.Patch(patch.Target, postfix: method);
                    }

                    AppliedProviderPatches[key] = patch;
                    activated++;
                }
                catch (Exception exception)
                {
                    ModLog.Warn(
                        $"激活提供者 {patch.ProviderId} 的呈现补丁失败：" +
                        exception.GetBaseException().Message);
                }
            }

            if (activated > 0)
            {
                ModLog.Info($"已激活 {activated} 个提供者附加功能补丁（语音、战斗动画等随皮肤生效）。");
            }
        }
    }

    /// <summary>提供者当前是否有已激活（随选中生效）的补丁。</summary>
    public static bool IsProviderActive(string providerId)
    {
        lock (CardPatchSync)
        {
            return AppliedProviderPatches.Values.Any(patch =>
                patch.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// 提供者的激活补丁是否接管角色呈现（模型 getter / AssetCache / CreateVisuals）。
    /// 这类提供者（如 sprite kit 的整套 2D 场景替换）激活时，本 Mod 不应再重建
    /// 选角展示，否则会用基线衍生场景覆盖提供者的呈现。
    /// </summary>
    public static bool ProviderControlsCharacterPresentation(string providerId)
    {
        lock (CardPatchSync)
        {
            return AppliedProviderPatches.Values.Any(patch =>
                patch.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase) &&
                patch.Target.DeclaringType != null &&
                (patch.Target.DeclaringType == typeof(AssetCache) ||
                 patch.Target.DeclaringType == typeof(AtlasManager) ||
                 typeof(CharacterModel).IsAssignableFrom(patch.Target.DeclaringType) ||
                 typeof(MonsterModel).IsAssignableFrom(patch.Target.DeclaringType)));
        }
    }

    private static void RememberRemovedProviderPatch(
        string providerRoot,
        MethodBase target,
        HarmonyLib.Patch patch,
        ProviderPatchKind kind)
    {
        lock (CardPatchSync)
        {
            if (RemovedProviderPatches.Any(existing =>
                    SameMethod(existing.Target, target) &&
                    existing.PatchMethod == patch.PatchMethod))
            {
                return;
            }

            RemovedProviderPatches.Add(new RemovedProviderPatch(
                providerRoot,
                ManagedSkinModLoader.GetProviderId(providerRoot) ?? string.Empty,
                target,
                patch.PatchMethod,
                kind,
                patch.priority,
                patch.before,
                patch.after));
        }
    }

    private static bool CanReapply(RemovedProviderPatch patch)
    {
        if (patch.ProviderId.Length == 0 ||
            patch.Kind is ProviderPatchKind.Transpiler or ProviderPatchKind.Finalizer)
        {
            return false;
        }

        var declaringType = patch.Target.DeclaringType;
        if (declaringType == null)
        {
            return false;
        }

        // 卡牌呈现由重放机制接管；卡牌贴图与远古背景由本 Mod 的资源机制接管，
        // 不重挂原始补丁。其余目标（含模型 getter、AssetCache、CreateVisuals 等
        // "整套皮肤机制"型前缀）在提供者被选中时整体恢复：前缀跳过原方法时，
        // 本 Mod 的 Priority.Last 后置补丁同样被跳过，正好让位给提供者。
        if (typeof(NCard).IsAssignableFrom(declaringType) ||
            typeof(CardModel).IsAssignableFrom(declaringType) ||
            typeof(EventModel).IsAssignableFrom(declaringType) ||
            declaringType == typeof(PreloadManager))
        {
            return false;
        }

        var namespaceName = declaringType.Namespace ?? string.Empty;
        return !namespaceName.StartsWith(
            "MegaCrit.Sts2.Core.Bindings.MegaSpine",
            StringComparison.Ordinal);
    }

    private static string ActivePatchKey(MethodBase target, MethodInfo patchMethod) =>
        target.Module.ModuleVersionId + ":" + target.MetadataToken +
        ">" + patchMethod.MetadataToken;

    public static int ReplaySelectedCardPostfixes(
        NCard card,
        MethodBase originalMethod,
        object[] originalArguments,
        string? providerRoot)
    {
        var root = providerRoot == null ? null : NormalizeRoot(providerRoot);
        if (root == null)
        {
            return 0;
        }

        RemovedCardPatch[] patches;
        lock (CardPatchSync)
        {
            patches = RemovedCardPatches
                .Where(patch => patch.ProviderRoot.Equals(root, StringComparison.OrdinalIgnoreCase) &&
                                IsCardPresentationStage(patch.Target, originalMethod))
                .ToArray();
        }

        var applied = 0;
        foreach (var patch in patches)
        {
            try
            {
                if (!TryBuildPatchArguments(
                        patch.PatchMethod,
                        card,
                        patch.Target,
                        SameMethod(patch.Target, originalMethod) ? originalArguments : [],
                        out var invokeArguments))
                {
                    WarnReplayOnce(
                        patch,
                        "参数包含无法在隔离呈现阶段还原的 Harmony 状态");
                    continue;
                }

                patch.PatchMethod.Invoke(null, invokeArguments);
                applied++;
            }
            catch (Exception exception)
            {
                WarnReplayOnce(patch, exception.GetBaseException().Message);
            }
        }

        return applied;
    }

    private static IEnumerable<(HarmonyLib.Patch Patch, ProviderPatchKind Kind)> EnumeratePatches(
        HarmonyLib.Patches patches)
    {
        foreach (var patch in patches.Prefixes)
        {
            yield return (patch, ProviderPatchKind.Prefix);
        }
        foreach (var patch in patches.Postfixes)
        {
            yield return (patch, ProviderPatchKind.Postfix);
        }
        foreach (var patch in patches.Transpilers)
        {
            yield return (patch, ProviderPatchKind.Transpiler);
        }
        foreach (var patch in patches.Finalizers)
        {
            yield return (patch, ProviderPatchKind.Finalizer);
        }
    }

    private static bool RememberCardPatch(string providerRoot, MethodBase target, MethodInfo patchMethod)
    {
        lock (CardPatchSync)
        {
            if (RemovedCardPatches.Any(patch =>
                    patch.ProviderRoot.Equals(providerRoot, StringComparison.OrdinalIgnoreCase) &&
                    SameMethod(patch.Target, target) &&
                    patch.PatchMethod == patchMethod))
            {
                return false;
            }

            RemovedCardPatches.Add(new RemovedCardPatch(providerRoot, target, patchMethod));
            return true;
        }
    }

    private static bool TryBuildPatchArguments(
        MethodInfo patchMethod,
        NCard card,
        MethodBase originalMethod,
        object[] originalArguments,
        out object?[] invokeArguments)
    {
        var targetParameters = originalMethod.GetParameters();
        invokeArguments = new object?[patchMethod.GetParameters().Length];
        for (var index = 0; index < invokeArguments.Length; index++)
        {
            var parameter = patchMethod.GetParameters()[index];
            var name = parameter.Name ?? string.Empty;
            var parameterType = parameter.ParameterType.IsByRef
                ? parameter.ParameterType.GetElementType()!
                : parameter.ParameterType;
            if (name == "__instance" || typeof(NCard).IsAssignableFrom(parameterType))
            {
                invokeArguments[index] = card;
                continue;
            }
            if (name == "__originalMethod")
            {
                invokeArguments[index] = originalMethod;
                continue;
            }
            if (name == "__args")
            {
                invokeArguments[index] = originalArguments;
                continue;
            }
            if (name.StartsWith("___", StringComparison.Ordinal))
            {
                var field = AccessTools.Field(originalMethod.DeclaringType, name[3..]);
                if (field == null)
                {
                    return false;
                }
                invokeArguments[index] = field.GetValue(card);
                continue;
            }
            if (name.StartsWith("__", StringComparison.Ordinal))
            {
                return false;
            }

            var targetIndex = Array.FindIndex(targetParameters, target => target.Name == name);
            if (targetIndex >= 0 && targetIndex < originalArguments.Length)
            {
                invokeArguments[index] = originalArguments[targetIndex];
                continue;
            }
            if (parameter.HasDefaultValue)
            {
                invokeArguments[index] = parameter.DefaultValue is DBNull ? null : parameter.DefaultValue;
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool SameMethod(MethodBase left, MethodBase right)
    {
        try
        {
            return left.Module == right.Module && left.MetadataToken == right.MetadataToken;
        }
        catch
        {
            return left == right;
        }
    }

    private static bool IsCardPresentationStage(MethodBase patchTarget, MethodBase currentTarget) =>
        SameMethod(patchTarget, currentTarget) ||
        (currentTarget.Name == nameof(NCard.UpdateVisuals) && patchTarget.Name == "Reload" &&
         typeof(NCard).IsAssignableFrom(patchTarget.DeclaringType));

    private static void WarnReplayOnce(RemovedCardPatch patch, string reason)
    {
        var key = patch.PatchMethod.Module.ModuleVersionId + ":" + patch.PatchMethod.MetadataToken;
        lock (CardPatchSync)
        {
            if (!ReplayWarnings.Add(key))
            {
                return;
            }
        }

        ModLog.Warn(
            $"无法隔离重放卡牌呈现补丁 {patch.PatchMethod.DeclaringType?.FullName}." +
            $"{patch.PatchMethod.Name}：{reason}");
    }

    private static bool IsVisualTarget(MethodBase target)
    {
        var declaringType = target.DeclaringType;
        if (declaringType == null)
        {
            return false;
        }

        if (declaringType == typeof(AssetCache))
        {
            return target.Name is "GetAsset" or "GetScene" or "GetTexture2D";
        }

        if (declaringType == typeof(AtlasManager))
        {
            return target.Name is "GetSprite" or "LoadAtlas" or "LoadAtlasInternal";
        }

        if (declaringType == typeof(SceneHelper) || declaringType == typeof(ImageHelper))
        {
            return target.Name.Contains("Path", StringComparison.OrdinalIgnoreCase) ||
                   target.Name.Contains("Load", StringComparison.OrdinalIgnoreCase) ||
                   target.Name.Contains("Instantiate", StringComparison.OrdinalIgnoreCase);
        }

        if (typeof(CharacterModel).IsAssignableFrom(declaringType) ||
            typeof(MonsterModel).IsAssignableFrom(declaringType) ||
            typeof(EventModel).IsAssignableFrom(declaringType) ||
            typeof(CardModel).IsAssignableFrom(declaringType))
        {
            return HasVisualName(target.Name) || ReturnsVisualValue(target);
        }

        if (typeof(NCreatureVisuals).IsAssignableFrom(declaringType))
        {
            return true;
        }

        if (typeof(NCard).IsAssignableFrom(declaringType))
        {
            return target.Name is "Reload" or "UpdateVisuals" or "UpdatePortrait" ||
                   HasVisualName(target.Name);
        }

        var namespaceName = declaringType.Namespace ?? string.Empty;
        // MegaSpine 绑定全部是骨骼/动画呈现 API，属于视觉接管范围。
        if (namespaceName.StartsWith("MegaCrit.Sts2.Core.Bindings.MegaSpine", StringComparison.Ordinal))
        {
            return true;
        }

        if (!namespaceName.StartsWith("MegaCrit.Sts2.Core.Nodes", StringComparison.Ordinal))
        {
            return false;
        }

        if (namespaceName.Contains(".Vfx", StringComparison.OrdinalIgnoreCase) ||
            VisualTypeNameTokens.Any(token =>
                declaringType.Name.Contains(token, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return HasVisualName(target.Name) &&
               (ReturnsVisualValue(target) ||
                target.GetParameters().Any(parameter => IsVisualValueType(parameter.ParameterType)));
    }

    private static bool HasVisualName(string name) =>
        VisualNameTokens.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static bool ReturnsVisualValue(MethodBase target) =>
        target is MethodInfo method && IsVisualValueType(method.ReturnType);

    private static bool IsVisualValueType(Type type)
    {
        if (type.IsByRef)
        {
            type = type.GetElementType()!;
        }

        return typeof(Texture2D).IsAssignableFrom(type) ||
               typeof(PackedScene).IsAssignableFrom(type) ||
               typeof(NCreatureVisuals).IsAssignableFrom(type);
    }

    private static string? GetProviderRoot(HarmonyLib.Patch patch, IReadOnlyList<string> roots)
    {
        var location = patch.PatchMethod.Module.Assembly.Location;
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        var assemblyPath = NormalizeRoot(location);
        if (assemblyPath == null)
        {
            return null;
        }

        // 嵌套 Mod 目录下取最长匹配的根，避免把子目录 Mod 的补丁归属到父目录 Mod。
        return roots
            .Where(root => assemblyPath.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(root => root.Length)
            .FirstOrDefault();
    }

    private static string? NormalizeRoot(string path)
    {
        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return null;
        }
    }

    private enum ProviderPatchKind
    {
        Prefix,
        Postfix,
        Transpiler,
        Finalizer
    }

    private sealed record RemovedCardPatch(
        string ProviderRoot,
        MethodBase Target,
        MethodInfo PatchMethod);

    private sealed record RemovedProviderPatch(
        string ProviderRoot,
        string ProviderId,
        MethodBase Target,
        MethodInfo PatchMethod,
        ProviderPatchKind Kind,
        int Priority,
        string[]? Before,
        string[]? After);
}
