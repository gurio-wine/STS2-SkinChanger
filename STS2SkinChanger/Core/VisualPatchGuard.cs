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

    public static int RemoveProviderVisualPatches(
        IEnumerable<string> providerRoots,
        IEnumerable<string>? preservedRuntimeRoots = null)
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
        var preservedRoots = (preservedRuntimeRoots ?? [])
            .Select(NormalizeRoot)
            .Where(root => root != null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removed = 0;
        var affectedTargets = new HashSet<string>(StringComparer.Ordinal);
        var affectedAssemblies = new Dictionary<string, int>(StringComparer.Ordinal);
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
                .Select(entry => (entry.Patch, entry.Kind, Root: GetProviderRoot(entry.Patch, roots, preservedRoots)))
                .Where(entry =>
                    entry.Root != null &&
                    !ManagedSkinModLoader.IsProviderAssemblyActive(
                        entry.Patch.PatchMethod.Module.Assembly))
                .DistinctBy(entry => (entry.Patch.PatchMethod, entry.Kind))
                .ToArray();
            foreach (var entry in providerPatches)
            {
                try
                {
                    Harmony.Unpatch(target, entry.Patch.PatchMethod);
                    removed++;
                    affectedTargets.Add($"{target.DeclaringType?.Name}.{target.Name}");
                    var source = entry.Patch.PatchMethod.Module.Assembly.GetName().Name ?? entry.Patch.owner;
                    affectedAssemblies[source] = affectedAssemblies.GetValueOrDefault(source) + 1;
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
            ModLog.Info(
                $"已移除 {removed} 个提供者皮肤呈现补丁，保留其余 DLL 功能；目标：" +
                string.Join("、", affectedTargets.OrderBy(name => name)) +
                "；来源：" + string.Join("、", affectedAssemblies.OrderBy(pair => pair.Key)
                    .Select(pair => $"{pair.Key}={pair.Value}")));
        }

        return removed;
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

        // Complete character skins frequently attach their shop/camp presentation from room
        // lifecycle callbacks instead of a CharacterModel getter.  These callbacks are cosmetic
        // only when they originate from a provider already classified by SkinCatalog, so keep
        // them in the same isolation set without relying on a particular mod name.
        if (declaringType.Name.EndsWith("MerchantRoom", StringComparison.OrdinalIgnoreCase) ||
            declaringType.Name.EndsWith("RestSiteRoom", StringComparison.OrdinalIgnoreCase))
        {
            return target.Name is "_Ready" or "Ready" || HasVisualName(target.Name);
        }

        // A full runtime provider may use combat-room creation to attach auxiliary visual nodes
        // after the creature itself has been constructed.
        if (declaringType.Name.EndsWith("CombatRoom", StringComparison.OrdinalIgnoreCase) &&
            target.Name.Equals("AddCreature", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Victory/defeat animation hooks are often installed on CombatManager, whose type and
        // namespace contain no visual token.  Provider roots are filtered below, so this does not
        // touch gameplay mods; it only prevents a recognized cosmetic provider's animation hook
        // from surviving after that provider is deselected.
        if (declaringType.Name.EndsWith("CombatManager", StringComparison.OrdinalIgnoreCase) &&
            target.Name.StartsWith("EndCombat", StringComparison.OrdinalIgnoreCase))
        {
            return true;
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

    private static string? GetProviderRoot(
        HarmonyLib.Patch patch,
        IReadOnlyList<string> roots,
        IReadOnlySet<string> preservedRoots)
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

        // Resolve ownership across both candidates and protected libraries. A nested gameplay
        // Mod must not inherit its parent directory's permission to remove cosmetic callbacks.
        var owner = roots.Concat(preservedRoots)
            .Where(root => assemblyPath.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(root => root.Length)
            .FirstOrDefault();
        return owner != null && !preservedRoots.Contains(owner) ? owner : null;
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

}
