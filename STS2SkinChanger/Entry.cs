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
        PatchAncientWaifusRuntime(harmony);
        ModLog.Info("代码补丁已加载。等待游戏资源初始化。");
    }

    internal static void PatchAncientWaifusRuntime(Harmony harmony)
    {
        var prefix = new HarmonyMethod(AccessTools.Method(typeof(Entry), nameof(SkipConflictingSkinRuntime)));
        var patched = 0;
        foreach (var target in AppDomain.CurrentDomain.GetAssemblies()
                     .SelectMany(GetLoadableTypes)
                     .Where(type => type.FullName?.EndsWith(
                         ".Core.GlobalTouchHook", StringComparison.Ordinal) == true)
                     .Select(type => AccessTools.Method(type, "RegisterHook"))
                     .Where(target => target != null)
                     .Cast<MethodInfo>()
                     .Distinct())
        {
            if (Harmony.GetPatchInfo(target)?.Prefixes.Any(patch => patch.owner == ModId) == true)
            {
                continue;
            }

            try
            {
                harmony.Patch(target, prefix: prefix);
                patched++;
            }
            catch (Exception exception)
            {
                ModLog.Warn(
                    $"无法停用冲突的皮肤运行时 {target.DeclaringType?.Assembly.GetName().Name}/" +
                    $"{target.DeclaringType?.FullName}.{target.Name}：{exception.Message}");
            }
        }

        if (patched > 0)
        {
            ModLog.Info($"已接管 {patched} 个皮肤运行时输入钩子，避免其再次覆盖已选外观。");
        }
    }

    private static bool SkipConflictingSkinRuntime() => false;

    internal static void PatchCardPortraitProviders(Harmony harmony)
    {
        var optionIds = SkinService.Catalog?.CardGroups
            .SelectMany(group => group.Options)
            .Select(option => option.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (optionIds == null || optionIds.Count == 0)
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
                .Where(patch => PatchBelongsToCardProvider(patch, optionIds))
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
        IReadOnlySet<string> optionIds)
    {
        var candidates = new[]
        {
            patch.owner,
            patch.PatchMethod.Module.Assembly.GetName().Name,
            patch.PatchMethod.DeclaringType?.FullName
        }.Select(NormalizeProviderIdentity)
            .Where(value => value.Length >= 4)
            .ToArray();

        return optionIds
            .Select(NormalizeProviderIdentity)
            .Where(value => value.Length >= 4)
            .Any(option => candidates.Any(candidate =>
                candidate.Contains(option, StringComparison.Ordinal) ||
                option.Contains(candidate, StringComparison.Ordinal)));
    }

    private static string NormalizeProviderIdentity(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
        catch
        {
            return [];
        }
    }
}

[HarmonyPatch(typeof(OneTimeInitialization), nameof(OneTimeInitialization.ExecuteEssential))]
internal static class EssentialInitializationPatch
{
    private static void Prefix()
    {
        Entry.PatchAncientWaifusRuntime(new Harmony(Entry.ModId));
        SkinService.InitializeBeforeAssets();
        Entry.PatchCardPortraitProviders(new Harmony(Entry.ModId));
    }

    private static void Postfix()
    {
        SkinService.InitializeCardGroupsAfterModels();
        Entry.PatchCardPortraitProviders(new Harmony(Entry.ModId));
    }
}
