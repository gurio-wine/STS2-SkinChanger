using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using System.Reflection;
using STS2SkinChanger.Catalog;

namespace STS2SkinChanger.Core;

/// <summary>
/// Loads bundled, behaviour-free API compatibility assemblies before third-party skin DLLs.
/// The adapters only satisfy CLR type contracts and expose a selection registry; Skin Changer
/// remains the sole owner of UI, resource routing and hot reload.
/// </summary>
internal static class FrameworkCompatibilityLayer
{
    private const string BundledAdapterFileName = "thunninoiSkinManager.dll";
    private const string RegistryTypeName =
        "thunninoiSkinManager.thunninoiSkinManagerCode.SkinRegistry";
    private static readonly HashSet<string> AvailableAssemblies =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> KnownFrameworkAssemblies =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly DeferredRegistrationQueue<Assembly> ProviderRegistrations = new();
    private static Assembly? _adapterAssembly;
    private static MethodInfo? _setActiveSkin;
    private static MethodInfo? _skinDbSetup;

    public static IReadOnlyCollection<string> CompatibilityAssemblyNames => AvailableAssemblies;

    public static void Initialize()
    {
        if (_adapterAssembly != null)
        {
            return;
        }

        try
        {
            var root = Path.GetDirectoryName(typeof(Entry).Assembly.Location);
            var adapterPath = root == null
                ? null
                : Path.GetFullPath(Path.Combine(root, BundledAdapterFileName));
            if (adapterPath == null || !File.Exists(adapterPath))
            {
                ModLog.Warn("未找到内置皮肤框架兼容层；声明依赖该框架的皮肤将交回游戏加载器。");
                return;
            }

            var identity = AssemblyName.GetAssemblyName(adapterPath);
            var assemblyName = identity.Name;
            if (string.IsNullOrWhiteSpace(assemblyName))
            {
                ModLog.Warn("内置皮肤框架兼容层没有有效的 CLR 程序集名称。");
                return;
            }

            KnownFrameworkAssemblies.Add(assemblyName);

            if (!ManagedSkinModLoader.CanInstallFrameworkCompatibilityAssembly(assemblyName))
            {
                ModLog.Info(
                    $"未启用内置皮肤框架兼容层 {assemblyName}：" +
                    "当前没有需要它的完整皮肤契约，或仍有依赖者必须使用原框架。");
                return;
            }

            var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate =>
                candidate.GetName().Name?.Equals(
                    assemblyName,
                    StringComparison.OrdinalIgnoreCase) == true);
            if (loaded != null)
            {
                var loadedPath = SafeAssemblyLocation(loaded);
                if (!loadedPath.Equals(adapterPath, StringComparison.OrdinalIgnoreCase))
                {
                    ModLog.Warn(
                        $"皮肤框架程序集 {assemblyName} 已由其它路径加载；" +
                        "本次会话保留原框架，不启用内置替代层。");
                    return;
                }

                _adapterAssembly = loaded;
            }
            else
            {
                _adapterAssembly = FrameworkAssemblyLoadContextPolicy.LoadFromAssemblyPath(
                    typeof(Entry).Assembly,
                    adapterPath);
            }

            var registry = _adapterAssembly.GetType(RegistryTypeName, throwOnError: true)!;
            _setActiveSkin = registry.GetMethod(
                "SetActiveSkin",
                BindingFlags.Static | BindingFlags.Public,
                binder: null,
                types: [typeof(ModelId), typeof(string)],
                modifiers: null);
            _skinDbSetup = registry.GetMethod(
                "SkinDbSetup",
                BindingFlags.Static | BindingFlags.Public,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
            if (_setActiveSkin == null || _skinDbSetup == null)
            {
                throw new MissingMethodException("皮肤框架兼容层缺少选择注册接口");
            }

            AvailableAssemblies.Add(assemblyName);
            ModLog.Info(
                $"已加载行为隔离的皮肤框架兼容层 {assemblyName}；" +
                "原管理器 UI、存档及全局资源补丁不会运行。");
        }
        catch (Exception exception)
        {
            _adapterAssembly = null;
            _setActiveSkin = null;
            _skinDbSetup = null;
            AvailableAssemblies.Clear();
            ModLog.Warn("加载内置皮肤框架兼容层失败：" + exception.GetBaseException().Message);
        }
    }

    public static bool IsBundledFrameworkHost(string? modId) =>
        modId != null && AvailableAssemblies.Contains(modId);

    public static bool IsKnownFrameworkHost(string? modId) =>
        modId != null && KnownFrameworkAssemblies.Contains(modId);

    public static void SynchronizeSelections(
        SkinCatalog catalog,
        IReadOnlyDictionary<string, string> selections)
    {
        if (_setActiveSkin == null)
        {
            return;
        }

        // ModelDb.AllCharacters dereferences the five built-in singleton entries and throws while
        // Essential initialization is still building the database. Contains(Type) is available in
        // both supported game versions and only checks the backing dictionary, so use it as the
        // readiness gate before touching those singleton getters.
        var modelDatabaseReady = ModelDb.Contains(typeof(Ironclad));
        if (modelDatabaseReady && _skinDbSetup != null)
        {
            var completed = ProviderRegistrations.RetryPending(
                isReady: true,
                _ => _skinDbSetup.Invoke(null, null),
                (providerKey, exception) => ModLog.Warn(
                    $"补做框架皮肤注册 {providerKey} 失败，已保留后续重试资格：" +
                    exception.GetBaseException().Message));
            if (completed > 0)
            {
                ModLog.Info($"模型库就绪后已补做 {completed} 个框架皮肤提供者注册。");
            }
        }

        var registeredCharacters = modelDatabaseReady
            ? ModelDb.AllCharacters
            : Enumerable.Empty<CharacterModel>();
        FrameworkSelectionSynchronizer.Synchronize(
            registeredCharacters,
            character => NormalizeToken(character.Id.Entry),
            groupId => catalog.TryGetSelectedFrameworkContract(
                groupId,
                selections.GetValueOrDefault(groupId),
                out var selected)
                ? selected.SkinId
                : null,
            (character, skinId) =>
                _setActiveSkin.Invoke(null, [character.Id, skinId]));
    }

    public static void NotifyProviderActivated(Assembly providerAssembly)
    {
        if (_skinDbSetup == null)
        {
            return;
        }

        var key = providerAssembly.FullName ?? providerAssembly.GetName().Name ?? string.Empty;
        try
        {
            var result = ProviderRegistrations.TryRegister(
                key,
                providerAssembly,
                ModelDb.Contains(typeof(Ironclad)),
                _ => _skinDbSetup.Invoke(null, null));
            if (result == DeferredRegistrationResult.Deferred)
            {
                ModLog.Info(
                    $"已延迟 {providerAssembly.GetName().Name} 的框架皮肤注册；" +
                    "游戏模型库就绪后将自动补做。");
            }
        }
        catch (Exception exception)
        {
            // Do not make a transient ModelDb timing failure tear down the provider's safe
            // animation/scene behavior. The queue deliberately retains this assembly and retries
            // before the next framework selection synchronization.
            ModLog.Warn(
                $"框架皮肤提供者 {providerAssembly.GetName().Name} 注册尚未完成，" +
                "已保留后续重试资格：" + exception.GetBaseException().Message);
        }
    }

    private static string SafeAssemblyLocation(Assembly assembly)
    {
        try
        {
            return Path.GetFullPath(assembly.Location);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeToken(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
