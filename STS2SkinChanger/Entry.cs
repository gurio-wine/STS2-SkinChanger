using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using STS2SkinChanger.Core;

namespace STS2SkinChanger;

[ModInitializer("Initialize")]
public static class Entry
{
    public const string ModId = "Gurio.SkinChanger";
    public const string LegacyModId = "STS2SkinChanger";

    public static bool IsSelfModId(string? modId) =>
        modId != null &&
        (modId.Equals(ModId, StringComparison.OrdinalIgnoreCase) ||
         modId.Equals(LegacyModId, StringComparison.OrdinalIgnoreCase));

    public static void Initialize()
    {
        ManagedSkinModLoader.Initialize();
        var harmony = new Harmony(ModId);
        try
        {
            harmony.PatchAll();
            ModLog.Info("代码补丁已加载。等待游戏资源初始化。");
        }
        catch (Exception exception)
        {
            // PatchAll 不是事务操作；失败时撤掉已安装的同 ID 补丁，避免半初始化状态。
            try
            {
                harmony.UnpatchAll(ModId);
            }
            catch (Exception rollbackException)
            {
                ModLog.Error("回滚已安装补丁失败：" + rollbackException.GetBaseException().Message);
            }

            ModLog.Error(
                "安装代码补丁失败，本 Mod 已停用本次会话的代码接管：" +
                exception.GetBaseException().Message);
        }
    }

}

[HarmonyPatch(typeof(OneTimeInitialization), nameof(OneTimeInitialization.ExecuteEssential))]
internal static class EssentialInitializationPatch
{
    private static void Prefix()
    {
        VisualPatchGuard.RemoveProviderVisualPatches(ManagedSkinModLoader.ProviderRoots);
        SkinService.InitializeBeforeAssets();
    }

    private static void Postfix()
    {
        VisualPatchGuard.RemoveProviderVisualPatches(ManagedSkinModLoader.ProviderRoots);
        SkinService.InitializeCardGroupsAfterModels();
        SkinService.PrepareSelectedCharacterPreviews();
        ExternalCardVisualBridge.WarmUp();
    }
}

[HarmonyPatch(typeof(ModManager), nameof(ModManager.GetModdedLocTables))]
internal static class CosmeticLocalizationOwnershipPatch
{
    // PCK namespaces remain mounted after a skin is deselected (and can also be mounted for
    // card art alone). Filter the game's merge inputs rather than trying to unload those files.
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(ref IEnumerable<string> __result) =>
        __result = SkinService.FilterModdedLocalizationTables(__result);
}

[HarmonyPatch(typeof(ReflectionHelper), nameof(ReflectionHelper.ModTypes), MethodType.Getter)]
internal static class DuplicateModelTypeCompatibilityPatch
{
    // Some older gameplay mods ship a model with the same ID as a model introduced by the
    // current game. ModelDb constructs both and aborts startup before any mod UI can appear.
    // Keep the game's canonical type whenever that ID already exists; unrelated mod-only models
    // and all non-model types remain untouched.
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(ref Type[] __result)
        => __result = ModelTypeCompatibility.Filter(__result);
}

internal static class ModelTypeCompatibility
{
    private static readonly Lazy<IReadOnlySet<ModelId>> CanonicalModelIds = new(BuildCanonicalModelIds);
    private static readonly HashSet<ModelId> ReportedCollisions = [];

    private static IReadOnlySet<ModelId> BuildCanonicalModelIds()
    {
        var ids = new HashSet<ModelId>();
        foreach (var type in typeof(AbstractModel).Assembly.GetTypes())
        {
            if (type.IsAbstract ||
                !type.IsSubclassOf(typeof(AbstractModel)))
            {
                continue;
            }

            try
            {
                ids.Add(ModelDb.GetId(type));
            }
            catch
            {
                // A malformed optional type must not prevent the compatibility filter from
                // protecting the rest of the model database.
            }
        }

        return ids;
    }

    internal static Type[] Filter(IEnumerable<Type> types)
    {
        var coreAssembly = typeof(AbstractModel).Assembly;
        return types.Where(type =>
        {
            if (type.Assembly == coreAssembly ||
                !type.IsSubclassOf(typeof(AbstractModel)))
            {
                return true;
            }

            ModelId id;
            try
            {
                id = ModelDb.GetId(type);
            }
            catch
            {
                return true;
            }

            if (!CanonicalModelIds.Value.Contains(id))
            {
                return true;
            }

            lock (ReportedCollisions)
            {
                if (ReportedCollisions.Add(id))
                {
                    ModLog.Warn($"检测到 Mod 模型 {id} 与当前游戏原版重复，已保留原版模型以避免启动失败。");
                }
            }

            return false;
        }).ToArray();
    }

    internal static int RemoveExistingCanonicalConflicts()
    {
        var coreAssembly = typeof(AbstractModel).Assembly;
        var removed = 0;
        foreach (var model in ModelDb.All.ToArray())
        {
            var type = model.GetType();
            if (type.Assembly == coreAssembly ||
                !CanonicalModelIds.Value.Contains(model.Id))
            {
                continue;
            }

            ModelDb.Remove(type);
            removed++;
            lock (ReportedCollisions)
            {
                if (ReportedCollisions.Add(model.Id))
                {
                    ModLog.Warn($"检测到已注入的 Mod 模型 {model.Id} 与当前游戏原版重复，已移除 Mod 实例并保留原版。");
                }
            }
        }

        return removed;
    }
}

// Some compatibility/framework mods pass an explicit model array to ModelDb.Init instead
// of letting the game read ReflectionHelper.ModTypes. In that path the property patch above
// is never consulted, so apply the same filtering at the actual initialization boundary.
[HarmonyPatch(typeof(ModelDb), nameof(ModelDb.Init))]
internal static class DuplicateModelInitCompatibilityPatch
{
    // Other mods may inject legacy models from their own Init prefix. Run after those
    // prefixes so the database and the explicit list are both in their final pre-init state.
    [HarmonyPriority(Priority.Last)]
    private static void Prefix(ref Type[]? __0)
    {
        var existingRemoved = ModelTypeCompatibility.RemoveExistingCanonicalConflicts();
        // The normal game call passes null and resolves AllAbstractModelSubtypes inside the
        // original method. Resolve it here too, so a previously cached/unfiltered reflection
        // list cannot bypass the compatibility filter.
        var candidates = __0 ?? ModelDb.AllAbstractModelSubtypes;
        var originalCount = candidates.Length;
        var filtered = ModelTypeCompatibility.Filter(candidates);
        var removedCount = originalCount - filtered.Length;
        __0 = filtered;
        ModLog.Info($"ModelDb.Init 兼容补丁已执行：模型 {originalCount} 个，移除列表重复 {removedCount} 个，移除已注入冲突 {existingRemoved} 个。");
    }
}
