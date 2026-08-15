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

            var providerPatches = patchInfo.Prefixes
                .Concat(patchInfo.Postfixes)
                .Concat(patchInfo.Transpilers)
                .Concat(patchInfo.Finalizers)
                .Where(patch => PatchBelongsToProvider(patch, roots))
                .DistinctBy(patch => patch.PatchMethod)
                .ToArray();
            foreach (var patch in providerPatches)
            {
                try
                {
                    Harmony.Unpatch(target, patch.PatchMethod);
                    removed++;
                    affectedTargets.Add($"{target.DeclaringType?.Name}.{target.Name}");
                }
                catch (Exception exception)
                {
                    ModLog.Warn(
                        $"无法移除视觉补丁 {patch.owner}/" +
                        $"{patch.PatchMethod.DeclaringType?.FullName}.{patch.PatchMethod.Name}：" +
                        exception.Message);
                }
            }
        }

        if (removed > 0)
        {
            ModLog.Info(
                $"已移除 {removed} 个提供者皮肤呈现补丁，保留其余 DLL 功能；目标：" +
                string.Join("、", affectedTargets.OrderBy(name => name)));
        }

        return removed;
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

    private static bool PatchBelongsToProvider(HarmonyLib.Patch patch, IReadOnlyList<string> roots)
    {
        var location = patch.PatchMethod.Module.Assembly.Location;
        if (string.IsNullOrWhiteSpace(location))
        {
            return false;
        }

        var assemblyPath = NormalizeRoot(location);
        return assemblyPath != null && roots.Any(root =>
            assemblyPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
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
}
