using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Cards;
using STS2SkinChanger.Core;
using System.Reflection;

namespace STS2SkinChanger;

[ModInitializer("Initialize")]
public static class Entry
{
    public const string ModId = "STS2SkinChanger";

    public static void Initialize()
    {
        var harmony = new Harmony(ModId);
        harmony.PatchAll();
        ModLog.Info("代码补丁已加载。等待游戏资源初始化。");
    }

    internal static void PatchCardPortraitProviders(Harmony harmony)
    {
        var providerRoots = SkinService.Catalog?.CardProviderRoots;
        if (providerRoots == null || providerRoots.Count == 0)
        {
            return;
        }

        var removed = 0;
        foreach (var target in Harmony.GetAllPatchedMethods().Where(IsCardArtTarget).ToArray())
        {
            var patchInfo = Harmony.GetPatchInfo(target);
            if (patchInfo == null)
            {
                continue;
            }

            var providerPatches = patchInfo.Prefixes
                .Concat(patchInfo.Postfixes)
                .Concat(patchInfo.Transpilers)
                .Concat(patchInfo.Finalizers)
                .Where(patch => PatchBelongsToCardProvider(patch, providerRoots))
                .DistinctBy(patch => patch.PatchMethod)
                .ToArray();
            foreach (var patch in providerPatches)
            {
                try
                {
                    harmony.Unpatch(target, patch.PatchMethod);
                    removed++;
                }
                catch (Exception exception)
                {
                    ModLog.Warn(
                        $"无法移除冲突的卡图补丁 {patch.owner}/" +
                        $"{patch.PatchMethod.DeclaringType?.FullName}.{patch.PatchMethod.Name}：" +
                        exception.Message);
                }
            }
        }

        if (removed > 0)
        {
            ModLog.Info($"已接管 {removed} 个卡图路径、贴图或节点补丁，改为按卡牌总览分类应用。");
        }
    }

    private static bool IsCardArtTarget(MethodBase target)
    {
        var typeName = target.DeclaringType?.FullName;
        if (typeName == typeof(MegaCrit.Sts2.Core.Models.CardModel).FullName)
        {
            return target.Name is "get_Portrait" or "get_PortraitPath";
        }

        if (typeName == typeof(NCard).FullName)
        {
            return target.Name is "Reload" or "UpdateVisuals" or "UpdatePortrait";
        }

        return typeName switch
        {
            "MegaCrit.Sts2.Core.Assets.AtlasManager" => target.Name == "GetSprite",
            "MegaCrit.Sts2.Core.Assets.AssetCache" =>
                target.Name is "GetAsset" or "GetScene" or "GetTexture2D",
            _ => false
        };
    }

    private static bool PatchBelongsToCardProvider(
        HarmonyLib.Patch patch,
        IReadOnlySet<string> providerRoots)
    {
        var location = patch.PatchMethod.Module.Assembly.Location;
        if (string.IsNullOrWhiteSpace(location))
        {
            return false;
        }

        try
        {
            var assemblyPath = Path.GetFullPath(location);
            return providerRoots.Any(root =>
            {
                var providerPrefix = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
                return assemblyPath.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase);
            });
        }
        catch
        {
            return false;
        }
    }
}

[HarmonyPatch(typeof(OneTimeInitialization), nameof(OneTimeInitialization.ExecuteEssential))]
internal static class EssentialInitializationPatch
{
    private static void Prefix()
    {
        SkinService.InitializeBeforeAssets();
        Entry.PatchCardPortraitProviders(new Harmony(Entry.ModId));
    }

    private static void Postfix()
    {
        SkinService.InitializeCardGroupsAfterModels();
        Entry.PatchCardPortraitProviders(new Harmony(Entry.ModId));
    }
}
