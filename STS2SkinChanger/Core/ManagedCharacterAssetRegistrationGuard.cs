using System.Reflection;
using HarmonyLib;
using STS2SkinChanger.Catalog;

namespace STS2SkinChanger.Core;

/// <summary>
/// Prevents a managed skin provider from leaving character asset paths in a framework-owned
/// global registry after the provider is no longer selected. Those registrations are owned by
/// the provider ID rather than by its Harmony assembly, so removing the provider's own patches is
/// insufficient: RitsuLib would continue routing combat, merchant, rest-site and UI assets to the
/// previous provider's private namespace.
/// </summary>
internal static class ManagedCharacterAssetRegistrationGuard
{
    private const string RegistryTypeName = "STS2RitsuLib.Content.ModContentRegistry";
    private const string FrameworkTypeName = "STS2RitsuLib.RitsuLibFramework";
    private static readonly object Sync = new();
    private static readonly Harmony Harmony = new(Entry.ModId + ".CharacterAssetRegistryGuard");
    private static readonly HashSet<MethodBase> PatchedRegistrationMethods = [];
    private static readonly HashSet<string> ManagedProviderIds =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, HashSet<string>> CharacterGroupsByProvider =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ReportedBlockedRegistrations =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<Type, PropertyInfo?> ModIdProperties = [];

    public static void Configure(SkinCatalog catalog)
    {
        lock (Sync)
        {
            ManagedProviderIds.Clear();
            CharacterGroupsByProvider.Clear();
            foreach (var group in catalog.Groups.Where(group =>
                         catalog.IsCharacterAppearanceGroup(group.Id)))
            {
                foreach (var option in group.Options.Where(option =>
                             !option.Id.Equals(SkinCatalog.BaseOptionId, StringComparison.OrdinalIgnoreCase)))
                {
                    var providerId = option.EffectiveProviderId;
                    ManagedProviderIds.Add(providerId);
                    if (!CharacterGroupsByProvider.TryGetValue(providerId, out var groupIds))
                    {
                        groupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        CharacterGroupsByProvider[providerId] = groupIds;
                    }

                    groupIds.Add(group.Id);
                }
            }

            EnsureFrameworkPatchesInstalled();
            ClearExistingRegistrations();
        }
    }

    public static void SuppressCurrentRegistrations()
    {
        lock (Sync)
        {
            EnsureFrameworkPatchesInstalled();
            ClearExistingRegistrations();
        }
    }

    private static void EnsureFrameworkPatchesInstalled()
    {
        var prefix = AccessTools.DeclaredMethod(
            typeof(ManagedCharacterAssetRegistrationGuard),
            nameof(RegistrationPrefix));
        if (prefix == null)
        {
            return;
        }

        foreach (var registryType in AppDomain.CurrentDomain.GetAssemblies()
                     .Select(assembly => assembly.GetType(RegistryTypeName, throwOnError: false))
                     .Where(type => type != null)
                     .Cast<Type>()
                     .Distinct())
        {
            foreach (var methodName in new[]
                     {
                         "RegisterCharacterAssetReplacement",
                         "RegisterGlobalCharacterAssetReplacement"
                     })
            {
                foreach (var target in registryType.GetMethods(
                             BindingFlags.Instance | BindingFlags.Public)
                         .Where(method => method.Name.Equals(methodName, StringComparison.Ordinal)))
                {
                    if (PatchedRegistrationMethods.Contains(target))
                    {
                        continue;
                    }

                    try
                    {
                        Harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                        PatchedRegistrationMethods.Add(target);
                        ModLog.Info(
                            $"已接管 {registryType.Assembly.GetName().Name} 的 {methodName} 注册入口。");
                    }
                    catch (Exception exception)
                    {
                        // RitsuLib is optional and ships multiple host-version assemblies. A
                        // missing or incompatible variant must never disable Skin Changer itself;
                        // the post-activation cleanup below remains a safe fallback.
                        ModLog.Warn(
                            $"接管 {registryType.Assembly.GetName().Name} 的 {methodName} 注册入口失败：" +
                            exception.GetBaseException().Message);
                    }
                }
            }
        }
    }

    private static bool RegistrationPrefix(object __instance)
    {
        string? providerId;
        lock (Sync)
        {
            providerId = GetRegistryModId(__instance);
            if (!ManagedCharacterAssetRegistrationPolicy.ShouldSuppress(
                    providerId,
                    ManagedProviderIds))
            {
                return true;
            }

            if (ReportedBlockedRegistrations.Add(providerId!))
            {
                ModLog.Info(
                    $"已阻止 {providerId} 向外部框架写入持久角色外观；" +
                    "战斗、商店、营火、头像和多人素材统一由当前皮肤选择路由。");
            }
        }

        return false;
    }

    private static string? GetRegistryModId(object registry)
    {
        var type = registry.GetType();
        if (!ModIdProperties.TryGetValue(type, out var property))
        {
            property = type.GetProperty("ModId", BindingFlags.Instance | BindingFlags.Public);
            ModIdProperties[type] = property;
        }

        return property?.GetValue(registry) as string;
    }

    private static void ClearExistingRegistrations()
    {
        if (ManagedProviderIds.Count == 0)
        {
            return;
        }

        foreach (var frameworkType in AppDomain.CurrentDomain.GetAssemblies()
                     .Select(assembly => assembly.GetType(FrameworkTypeName, throwOnError: false))
                     .Where(type => type != null)
                     .Cast<Type>()
                     .Distinct())
        {
            var getRegistry = frameworkType.GetMethod(
                "GetContentRegistry",
                BindingFlags.Static | BindingFlags.Public,
                binder: null,
                types: [typeof(string)],
                modifiers: null);
            if (getRegistry == null)
            {
                continue;
            }

            foreach (var providerId in ManagedProviderIds)
            {
                try
                {
                    var registry = getRegistry.Invoke(null, [providerId]);
                    if (registry == null)
                    {
                        continue;
                    }

                    var removed = InvokeBoolean(registry, "ClearGlobalCharacterAssetReplacement");
                    if (CharacterGroupsByProvider.TryGetValue(providerId, out var groupIds))
                    {
                        foreach (var groupId in groupIds)
                        {
                            removed |= InvokeBoolean(
                                registry,
                                "RemoveCharacterAssetReplacement",
                                groupId);
                        }
                    }

                    if (removed)
                    {
                        ModLog.Info(
                            $"已清除 {providerId} 遗留在外部框架中的角色外观注册，" +
                            "避免切换后继续污染其它皮肤与场景。");
                    }
                }
                catch (Exception exception)
                {
                    ModLog.Warn(
                        $"清理 {providerId} 的外部角色外观注册失败：" +
                        exception.GetBaseException().Message);
                }
            }
        }
    }

    private static bool InvokeBoolean(object target, string methodName, params object[] arguments)
    {
        var argumentTypes = arguments.Select(argument => argument.GetType()).ToArray();
        var method = target.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: argumentTypes,
            modifiers: null);
        return method?.Invoke(target, arguments) is true;
    }
}
