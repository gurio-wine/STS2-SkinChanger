using MegaCrit.Sts2.Core.Models;
using System.Reflection;
using System.Runtime.Loader;
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
    private static readonly HashSet<string> DeclaredProviderAssemblies =
        new(StringComparer.OrdinalIgnoreCase);
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
                _adapterAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(adapterPath);
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

        foreach (var character in ModelDb.AllCharacters)
        {
            var groupId = NormalizeToken(character.Id.Entry);
            var contract = catalog.TryGetSelectedFrameworkContract(
                groupId,
                selections.GetValueOrDefault(groupId),
                out var selected)
                ? selected
                : null;
            _setActiveSkin.Invoke(null, [character.Id, contract?.SkinId ?? "default"]);
        }
    }

    public static void NotifyProviderActivated(Assembly providerAssembly)
    {
        if (_skinDbSetup == null)
        {
            return;
        }

        var key = providerAssembly.FullName ?? providerAssembly.GetName().Name ?? string.Empty;
        if (!DeclaredProviderAssemblies.Add(key))
        {
            return;
        }

        // Provider registration is a Harmony postfix on this empty declaration hook. Calling it
        // after the selected provider's PatchAll preserves its own descriptor objects without
        // ever starting the original manager.
        _skinDbSetup.Invoke(null, null);
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
