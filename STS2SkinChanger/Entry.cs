using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using STS2SkinChanger.Core;
using System.Reflection;

namespace STS2SkinChanger;

[ModInitializer("Initialize")]
public static class Entry
{
    public const string ModId = "Gurio.SkinChanger";
    public const string LegacyModId = "STS2SkinChanger";
    // Four-part versions are kept out of the game's manifest version field because older
    // loaders only accept three-part SemanticVersion values. This marker is embedded in the
    // assembly and printed at startup so an internal deployment can be identified unambiguously.
    public const string InternalTestVersion = "0.9.119.2";

    public static bool IsSelfModId(string? modId) =>
        modId != null &&
        (modId.Equals(ModId, StringComparison.OrdinalIgnoreCase) ||
         modId.Equals(LegacyModId, StringComparison.OrdinalIgnoreCase));

    public static void Initialize()
    {
        var assembly = typeof(Entry).Assembly;
        ModLog.Info(
            $"内测版本 {InternalTestVersion}；程序集={assembly.GetName().Name} " +
            $"{assembly.GetName().Version}；路径={assembly.Location}");
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
    // PCK namespaces remain mounted after a character skin is deselected. Filter only its
    // characters.json table; events, cards and all other Mod text keep their normal lifetime.
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
    private static readonly PropertyInfo? RegisteredModelsProperty =
        typeof(ModelDb).GetProperty(
            "All",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly FieldInfo? RegisteredModelsField =
        typeof(ModelDb).GetField(
            "_contentById",
            BindingFlags.Static | BindingFlags.NonPublic);

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

    internal static Type[] Filter(
        IEnumerable<Type> types,
        bool includeAlreadyRegisteredModels = false)
    {
        var coreAssembly = typeof(AbstractModel).Assembly;
        // ReflectionHelper.ModTypes is also used after ModelDb.Init by frameworks such as
        // BaseLib to discover custom character scene conversions. Seeding this set from the
        // live ModelDb in that path made every legitimate, already-initialized Mod model look
        // like a duplicate and removed it from all later reflection scans. Only the actual
        // ModelDb.Init boundary needs to account for models injected by earlier prefixes.
        var seenIds = includeAlreadyRegisteredModels
            ? GetRegisteredModels().Select(model => model.Id).ToHashSet()
            : [];
        var filtered = new List<Type>();

        foreach (var type in types)
        {
            if (type.Assembly == coreAssembly ||
                !type.IsSubclassOf(typeof(AbstractModel)))
            {
                filtered.Add(type);
                continue;
            }

            ModelId id;
            try
            {
                id = ModelDb.GetId(type);
            }
            catch
            {
                filtered.Add(type);
                continue;
            }

            // A mod type with a canonical game ID can never replace the game's type.
            // For IDs that are not present in the game, also de-duplicate competing mod
            // types: the first loaded provider owns the ID and later providers are skipped.
            if (CanonicalModelIds.Value.Contains(id) || !seenIds.Add(id))
            {
                ReportCollision(id);
                continue;
            }

            filtered.Add(type);
        }

        return filtered.ToArray();
    }

    private static void ReportCollision(ModelId id)
    {
        lock (ReportedCollisions)
        {
            if (ReportedCollisions.Add(id))
            {
                ModLog.Warn($"检测到重复 Mod 模型 ID {id}，已保留先注册的模型以避免启动失败。");
            }
        }
    }

    internal static int RemoveExistingCanonicalConflicts()
    {
        var coreAssembly = typeof(AbstractModel).Assembly;
        var removed = 0;
        foreach (var model in GetRegisteredModels().ToArray())
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

    private static IEnumerable<AbstractModel> GetRegisteredModels()
    {
        if (RegisteredModelsProperty?.GetValue(null) is IEnumerable<AbstractModel> models)
        {
            return models;
        }

        if (RegisteredModelsField?.GetValue(null) is IDictionary<ModelId, AbstractModel> modelMap)
        {
            return modelMap.Values;
        }

        return [];
    }
}

// Some compatibility/framework mods pass an explicit model array to ModelDb.Init instead
// of letting the game read ReflectionHelper.ModTypes. In that path the property patch above
// is never consulted, so apply the same filtering at the actual initialization boundary.
// The formal v0.107.1 build exposes Init() while the public beta exposes Init(Type[]?). Resolve
// the method dynamically and use Harmony's __args bridge so one DLL can load on both branches.
[HarmonyPatch]
internal static class DuplicateModelInitCompatibilityPatch
{
    private static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(ModelDb), nameof(ModelDb.Init)) ??
        throw new MissingMethodException(typeof(ModelDb).FullName, nameof(ModelDb.Init));

    // Other mods may inject legacy models from their own Init prefix. Run after those
    // prefixes so the database and the explicit list are both in their final pre-init state.
    [HarmonyPriority(Priority.Last)]
    private static void Prefix(object[] __args)
    {
        var existingRemoved = ModelTypeCompatibility.RemoveExistingCanonicalConflicts();
        // The normal game call passes null and resolves AllAbstractModelSubtypes inside the
        // original method. Resolve it here too, so a previously cached/unfiltered reflection
        // list cannot bypass the compatibility filter.
        var candidates = __args is [{ } firstArgument] && firstArgument is Type[] injected
            ? injected
            : ModelDb.AllAbstractModelSubtypes;
        var originalCount = candidates.Length;
        var filtered = ModelTypeCompatibility.Filter(
            candidates,
            includeAlreadyRegisteredModels: true);
        var removedCount = originalCount - filtered.Length;
        if (__args.Length > 0)
        {
            __args[0] = filtered;
        }

        ModLog.Info($"ModelDb.Init 兼容补丁已执行：模型 {originalCount} 个，移除列表重复 {removedCount} 个，移除已注入冲突 {existingRemoved} 个。");
    }
}
