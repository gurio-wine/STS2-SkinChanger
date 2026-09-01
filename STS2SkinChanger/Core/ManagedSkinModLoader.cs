using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using STS2SkinChanger.Catalog;
using System.Reflection;
using System.Runtime.Loader;

namespace STS2SkinChanger.Core;

internal static class ManagedSkinModLoader
{
    private static readonly MethodInfo? InvokeOnModDetectedMethod =
        AccessTools.Method(typeof(ModManager), "InvokeOnModDetected");
    private static readonly FieldInfo? OnModDetectedField =
        AccessTools.Field(typeof(ModManager), "OnModDetected");
    private static readonly FieldInfo? GameVersionField =
        AccessTools.Field(typeof(ModManager), "_gameVersion");
    private static readonly FieldInfo? CircularDependenciesField =
        AccessTools.Field(typeof(ModManager), "_circularDependencies");
    private static readonly Dictionary<string, SkinProviderProbe> ProvidersByRoot =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> NegativeProviderRoots =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> RegisteredProviderAssemblies =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ProviderAssembly> ProviderAssemblies =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Assembly> LoadedProviderAssemblies =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, IReadOnlyList<ManagedVisualPostfix>> VisualPostfixesByProvider =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ActiveProviderRuntime> ActiveProviderRuntimes =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ProviderRuntimeBlueprint> ProviderRuntimeBlueprints =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<Assembly, string> ScopedMonsterProviderAssemblies = new();
    private static readonly HashSet<MethodBase> ScopedMonsterIsEnabledMethods = [];
    private static readonly HashSet<MethodBase> ScopedMonsterSetEnabledMethods = [];
    private static readonly Harmony ScopedMonsterSelectionHarmony =
        new($"{Entry.ModId}.scoped-monster-selection");
    // ModInitializer methods are commonly used to subscribe to SceneTree signals.  Harmony can
    // remove the provider's patches when it is deselected, but it cannot undo a direct C# event
    // subscription. Remember successful initializers so a hot re-selection does not register the
    // same signal twice (or throw before the scene can be rebuilt).
    private static readonly HashSet<string> InvokedProviderInitializers =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ReportedVisualPostfixes =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> FailedVisualPostfixes =
        new(StringComparer.OrdinalIgnoreCase);
    private static bool _initialized;
    private static bool _reflectionTargetsReady;

    public static bool IsBeforeAllSkinProviders { get; private set; } = true;
    public static IReadOnlyList<Mod> SkinProvidersBeforeSelf { get; private set; } = [];
    public static IReadOnlyList<Mod> SkinProvidersInLoadOrder { get; private set; } = [];
    public static IReadOnlyCollection<string> ProviderRoots => ProvidersByRoot.Keys;

    public static bool IsProviderAssemblyActive(Assembly assembly) =>
        ActiveProviderRuntimes.Values.Any(runtime => ReferenceEquals(runtime.Assembly, assembly));

    public static void EnsureScopedMonsterSelectionRouter(string providerId)
    {
        if (!ProviderAssemblies.TryGetValue(providerId, out var provider))
        {
            return;
        }

        try
        {
            var assembly = GetOrLoadProviderAssembly(provider);
            if (assembly == null)
            {
                return;
            }

            ScopedMonsterProviderAssemblies[assembly] = providerId;
            var prefix = new HarmonyMethod(AccessTools.Method(
                typeof(ManagedSkinModLoader),
                nameof(ScopedMonsterIsEnabledPrefix)));
            var setterPostfix = new HarmonyMethod(AccessTools.Method(
                typeof(ManagedSkinModLoader),
                nameof(ScopedMonsterSetEnabledPostfix)));
            var patched = 0;
            var methods = GetLoadableTypes(assembly)
                .SelectMany(type => type.GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly))
                .ToArray();
            foreach (var method in methods.Where(IsScopedMonsterIsEnabledMethod))
            {
                if (!ScopedMonsterIsEnabledMethods.Add(method))
                {
                    continue;
                }

                ScopedMonsterSelectionHarmony.Patch(method, prefix: prefix);
                patched++;
            }

            var settersPatched = 0;
            foreach (var method in methods.Where(IsScopedMonsterSetEnabledMethod))
            {
                if (!ScopedMonsterSetEnabledMethods.Add(method))
                {
                    continue;
                }

                ScopedMonsterSelectionHarmony.Patch(method, postfix: setterPostfix);
                settersPatched++;
            }

            if (patched > 0 || settersPatched > 0)
            {
                ModLog.Info(
                    $"已接管 {provider.Name} 的逐怪物启用判断；" +
                    $"{patched} 个读取入口、{settersPatched} 个写入入口现在直接跟随" +
                    "每个怪物在皮肤切换器中的选择。");
            }
        }
        catch (Exception exception)
        {
            ModLog.Warn(
                $"接管 {provider.Name} 的逐怪物启用判断失败，将保留原作者配置：" +
                exception.GetBaseException().Message);
        }
    }

    private static bool IsScopedMonsterIsEnabledMethod(MethodInfo method)
    {
        if (!method.Name.Equals("IsEnabled", StringComparison.Ordinal) ||
            method.ReturnType != typeof(bool) ||
            method.GetParameters() is not [{ ParameterType: var profileType }])
        {
            return false;
        }

        var targetProperty = profileType.GetProperty(
            "Target",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return targetProperty?.PropertyType.GetProperty(
            "MonsterId",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.PropertyType == typeof(string);
    }

    private static bool IsScopedMonsterSetEnabledMethod(MethodInfo method)
    {
        if (!method.Name.Equals("SetEnabled", StringComparison.Ordinal) ||
            method.ReturnType != typeof(void) ||
            method.GetParameters() is not
                [{ ParameterType: var profileType }, { ParameterType: var enabledType }] ||
            enabledType != typeof(bool))
        {
            return false;
        }

        var targetProperty = profileType.GetProperty(
            "Target",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return targetProperty?.PropertyType.GetProperty(
            "MonsterId",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.PropertyType == typeof(string);
    }

    private static bool ScopedMonsterIsEnabledPrefix(
        MethodBase __originalMethod,
        object __0,
        ref bool __result)
    {
        try
        {
            var assembly = __originalMethod.DeclaringType?.Assembly;
            if (assembly == null ||
                !ScopedMonsterProviderAssemblies.TryGetValue(assembly, out var providerId))
            {
                return true;
            }

            var target = __0.GetType().GetProperty(
                "Target",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(__0);
            var monsterId = target?.GetType().GetProperty(
                "MonsterId",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target) as string;
            if (string.IsNullOrWhiteSpace(monsterId))
            {
                return true;
            }

            __result = SkinService.IsScopedMonsterRuntimeProviderSelected(providerId, monsterId);
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static void ScopedMonsterSetEnabledPostfix(
        MethodBase __originalMethod,
        object __0,
        bool __1)
    {
        try
        {
            var assembly = __originalMethod.DeclaringType?.Assembly;
            if (assembly == null ||
                !ScopedMonsterProviderAssemblies.TryGetValue(assembly, out var providerId))
            {
                return;
            }

            var target = __0.GetType().GetProperty(
                "Target",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(__0);
            var monsterId = target?.GetType().GetProperty(
                "MonsterId",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target) as string;
            if (!string.IsNullOrWhiteSpace(monsterId))
            {
                SkinService.ApplyScopedMonsterRuntimeProviderSelection(providerId, monsterId, __1);
            }
        }
        catch (Exception exception)
        {
            ModLog.Warn(
                "同步提供者怪物开关失败：" + exception.GetBaseException().Message);
        }
    }

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        // 在产生任何副作用前预检游戏内部反射目标，避免运行到一半因句柄缺失而进入"脏回退"。
        _reflectionTargetsReady = (InvokeOnModDetectedMethod != null || OnModDetectedField != null) &&
                                  GameVersionField != null &&
                                  CircularDependenciesField != null;
        if (!_reflectionTargetsReady)
        {
            ModLog.Error(
                "无法解析游戏内部加载器接口，托管加载模式已禁用（游戏版本可能不兼容）。" +
                "皮肤切换仍可工作，但 DLL 皮肤提供者的呈现补丁不会被接管。");
        }

        CleanupOldProviderNamespaces();
        var mods = ModManager.Mods.ToArray();
        var descriptors = mods
            .Where(mod => mod.state is ModLoadState.None or ModLoadState.Loaded)
            .Where(mod => mod.manifest is { id: not null })
            .Where(mod => !Entry.IsSelfModId(mod.manifest!.id))
            .Select(ToDescriptor)
            .ToArray();
        var probes = SkinCatalog.ProbeSkinProviders(descriptors);
        foreach (var probe in probes)
        {
            if (probe.RootPath == null)
            {
                continue;
            }

            ProvidersByRoot[NormalizePath(probe.RootPath)] = probe;
        }

        var selfIndex = Array.FindIndex(mods, mod => Entry.IsSelfModId(mod.manifest?.id));
        SkinProvidersInLoadOrder = mods
            .Where(mod => !Entry.IsSelfModId(mod.manifest?.id))
            .Where(mod => IsManagedProvider(mod, out _))
            .ToArray();
        SkinProvidersBeforeSelf = selfIndex < 0
            ? SkinProvidersInLoadOrder
            : mods.Take(selfIndex)
                .Where(mod => !Entry.IsSelfModId(mod.manifest?.id))
                .Where(mod => IsManagedProvider(mod, out _))
                .ToArray();
        IsBeforeAllSkinProviders = selfIndex >= 0 && SkinProvidersBeforeSelf.Count == 0;
        if (SkinProvidersBeforeSelf.Count > 0)
        {
            var alreadyLoaded = SkinProvidersBeforeSelf
                .Select(mod => mod.manifest?.name ?? mod.manifest?.id ?? mod.path)
                .ToArray();
            ModLog.Warn(
                "托管加载模式仅能拦截排在本 Mod 后面的皮肤提供者。" +
                "请把皮肤切换器-Skin Changer 移到所有皮肤 Mod 之前并重启，无需移到全部 Mod 最前。" +
                $" 本次已提前加载：{string.Join("、", alreadyLoaded)}");
        }

        ModLog.Info(
            $"托管加载模式已识别 {ProvidersByRoot.Count} 个皮肤提供者；" +
            "PCK 资源由本 Mod 按选择接管；运行时私有依赖会在首次选中时低优先级挂载，" +
            "DLL 行为补丁仅在选中期间启用。");
    }

    public static bool TryManage(Mod mod)
    {
        if (!_reflectionTargetsReady ||
            mod.state != ModLoadState.None ||
            !IsManagedProvider(mod, out var provider))
        {
            return false;
        }

        try
        {
            if (!CanBypassOriginalLoader(mod))
            {
                return false;
            }

            if (mod.manifest?.version != null &&
                SemanticVersion.TryFromString(mod.manifest.version, out var version))
            {
                mod.version = version;
            }

            RememberProviderAssembly(mod, provider);
            mod.state = ModLoadState.Loaded;
            NotifyModDetected(mod);
            ModLog.Info(
                $"已隔离皮肤提供者 {mod.manifest?.name ?? mod.manifest?.id}：" +
                $"视觉组={provider.VisualGroupCount}, 卡图={provider.CardAssetCount}, " +
                $"卡牌呈现={provider.CardPresentationCount}, 独立图片={provider.RuntimeImageCount}, " +
                $"场景脚本={provider.ManagedScriptCount}, " +
                $"交互场景={(provider.HasInteractiveScenes ? "是" : "否")}；" +
                "原 PCK 未全局挂载；" +
                "只有选中该皮肤时，才会按需调用与模型 CreateVisuals 明确绑定的视觉后处理；" +
                (provider.ManagedScriptCount > 0
                    ? "只有选中该皮肤时才注册场景实例化所需的 Godot 脚本类型；"
                    : string.Empty) +
                "完整视觉整包或含交互场景的提供者会在选中期间临时启用原作者行为，" +
                "其资源替换与卡牌呈现仍由皮肤切换器自身接管。");
            return true;
        }
        catch (Exception exception)
        {
            mod.state = ModLoadState.None;
            ModLog.Warn(
                $"托管 {mod.manifest?.name ?? mod.manifest?.id} 失败，将交回游戏原加载器：" +
                exception.GetBaseException().Message);
            return false;
        }
    }

    private static void RememberProviderAssembly(Mod mod, SkinProviderProbe provider)
    {
        if (mod.manifest is not { hasDll: true, id: not null })
        {
            return;
        }

        var assemblyPath = Path.GetFullPath(Path.Combine(mod.path, mod.manifest.id + ".dll"));
        if (!File.Exists(assemblyPath))
        {
            ModLog.Warn($"找不到皮肤提供者程序集 {assemblyPath}。");
            return;
        }

        var hasDeclarativeCharacterAssetReplacement =
            ManagedCharacterAssetReplacementScanner.Scan(mod.path, mod.manifest.id).Count > 0;
        ProviderAssemblies[mod.manifest.id] = new ProviderAssembly(
            assemblyPath,
            mod.manifest.name ?? mod.manifest.id,
            provider.ManagedScriptCount > 0 || hasDeclarativeCharacterAssetReplacement,
            hasDeclarativeCharacterAssetReplacement);
    }

    public static bool EnsureProviderGodotScripts(string providerId)
    {
        if (!ProviderAssemblies.TryGetValue(providerId, out var provider) ||
            !provider.HasGodotScripts)
        {
            return false;
        }

        try
        {
            var assembly = GetOrLoadProviderAssembly(provider);
            if (assembly == null)
            {
                return false;
            }

            if (!RegisteredProviderAssemblies.Add(provider.AssemblyPath))
            {
                return true;
            }

            var bridgeType = typeof(GodotObject).Assembly.GetType("Godot.Bridge.ScriptManagerBridge");
            var lookupMethod = bridgeType?.GetMethods(
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name.Equals("LookupScriptsInAssembly", StringComparison.Ordinal) &&
                    method.GetParameters() is [{ ParameterType: var parameterType }] &&
                    parameterType == typeof(Assembly));
            if (lookupMethod == null)
            {
                RegisteredProviderAssemblies.Remove(provider.AssemblyPath);
                ModLog.Warn(
                    $"无法找到 Godot 场景脚本注册入口，{provider.Name} " +
                    "的自定义场景脚本可能无法实例化。");
                return false;
            }

            lookupMethod.Invoke(null, [assembly]);
            ModLog.Info(
                $"已按当前选择注册 {provider.Name} 的 Godot 场景脚本类型，" +
                "此注册步骤本身未执行其初始化器或 Harmony 补丁。");
            return true;
        }
        catch (Exception exception)
        {
            if (IsAlreadyRegisteredGodotScriptException(exception))
            {
                // The game or another legitimate loader may have discovered the same assembly
                // first. ScriptManagerBridge uses a unique resource-path dictionary and reports
                // that harmless race as a duplicate-key ArgumentException. Keep our local marker
                // so every hot reload does not retry the same registration and emit another error.
                ModLog.Info(
                    $"{provider.Name} 的 Godot 场景脚本已经注册，复用现有注册结果。");
                return true;
            }

            RegisteredProviderAssemblies.Remove(provider.AssemblyPath);
            ModLog.Warn(
                $"注册 {provider.Name} 的 Godot 场景脚本失败；" +
                "仍会隔离其全局视觉补丁：" +
                exception.GetBaseException().Message);
            return false;
        }
    }

    private static bool IsAlreadyRegisteredGodotScriptException(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is not ArgumentException)
            {
                continue;
            }

            var message = current.Message;
            if (message.Contains("same key", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("already been added", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("相同的键", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("已添加", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Removes every Harmony callback installed by a managed provider that is no longer selected.
    /// Unpatching by callback method instead of Harmony owner is important because third-party mods
    /// sometimes reuse the same owner string; only the selected provider's assembly is touched.
    /// </summary>
    public static void DeactivateProvidersExcept(IEnumerable<string> selectedProviderIds)
    {
        var selected = selectedProviderIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var providerId in ActiveProviderRuntimes.Keys
                     .Where(providerId => !selected.Contains(providerId))
                     .ToArray())
        {
            DeactivateProvider(providerId);
        }
    }

    /// <summary>
    /// Activates the original initializer of each selected runtime provider after its required PCK
    /// resources have been mounted. Resource replacement callbacks are then removed because Skin
    /// Changer owns those entry points; input, animation and scene behaviour remains active until
    /// deselection. This also supports providers that expose independently selectable cards.
    /// </summary>
    public static void ActivateSelectedProviders(IEnumerable<string> selectedProviderIds)
    {
        foreach (var providerId in selectedProviderIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (ActiveProviderRuntimes.ContainsKey(providerId) ||
                !ProviderAssemblies.TryGetValue(providerId, out var provider))
            {
                continue;
            }

            ActivateProvider(providerId, provider);
        }
    }

    private static void ActivateProvider(string providerId, ProviderAssembly provider)
    {
        Assembly? assembly = null;
        try
        {
            assembly = GetOrLoadProviderAssembly(provider);
            if (assembly == null)
            {
                return;
            }

            var initializerTypes = provider.HasDeclarativeCharacterAssetReplacement
                ? []
                : GetLoadableTypes(assembly)
                    .Select(type => (Type: type, Attribute: type.GetCustomAttribute<ModInitializerAttribute>()))
                    .Where(pair => pair.Attribute != null)
                    .ToArray();
            var skippedInitializer = false;
            if (initializerTypes.Length == 0)
            {
                // There is no original initializer left to register managed Godot scripts (this
                // is also the intentional path for declarative providers), so do it immediately
                // before PatchAll/scene use.
                EnsureProviderGodotScripts(providerId);
                new Harmony($"{Entry.ModId}.selected.{NormalizeHarmonyId(providerId)}")
                    .PatchAll(assembly);
                if (provider.HasDeclarativeCharacterAssetReplacement)
                {
                    ModLog.Info(
                        $"{provider.Name} 使用框架式角色资源注册；已由皮肤切换器直接路由资源，" +
                        "因此未执行会产生全局强制替换的原初始化器。");
                }
            }
            else
            {
                foreach (var initializer in initializerTypes)
                {
                    var method = initializer.Type.GetMethod(
                        initializer.Attribute!.initializerMethod,
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (method == null || method.GetParameters().Length != 0)
                    {
                        throw new MissingMethodException(
                            initializer.Type.FullName,
                            initializer.Attribute.initializerMethod);
                    }

                    var initializerKey = BuildInitializerKey(
                        providerId,
                        assembly,
                        initializer.Type,
                        method);
                    if (InvokedProviderInitializers.Contains(initializerKey))
                    {
                        skippedInitializer = true;
                        ModLog.Info(
                            $"已跳过 {provider.Name} 的重复初始化器 " +
                            $"{initializer.Type.FullName}.{method.Name}。");
                        continue;
                    }

                    method.Invoke(null, null);
                    InvokedProviderInitializers.Add(initializerKey);
                }

                // The first run may install Harmony patches from inside a custom initializer.
                // After deselection those callbacks have been removed, so restore attribute-based
                // patches without invoking the initializer (and its non-Harmony subscriptions)
                // for a second time. Restore the exact callbacks that the provider actually chose
                // on its first activation: some providers deliberately exclude optional profiling
                // or compatibility patch classes, which a blanket PatchAll would incorrectly enable.
                if (skippedInitializer)
                {
                    if (ProviderRuntimeBlueprints.TryGetValue(providerId, out var blueprint))
                    {
                        PatchProviderCallbacks(blueprint.BehaviorPatches);
                    }
                    else
                    {
                        new Harmony($"{Entry.ModId}.selected.{NormalizeHarmonyId(providerId)}")
                            .PatchAll(assembly);
                    }
                }
            }

            var installedPatches = CaptureProviderPatches(assembly);
            // Keep the complete set of node-ready presentation callbacks.  Existing nodes still
            // replay only postfixes (see ReplaySelectedNodeReadyBehavior), while freshly created
            // visual nodes may safely replay their visual prefixes as well.  Merchant skins such
            // as ATA and Merchant2CuteII put their actual skeleton/scale replacement in a Prefix;
            // dropping it here made every hot-created merchant silently fall back to vanilla.
            var presentationPatches = installedPatches
                .Where(IsReplayableCharacterPresentationPatch)
                .ToArray();
            var nodeReadyPresentationPatches = installedPatches
                .Where(patch => IsNodeReadyPresentationTarget(patch.Target))
                .ToArray();
            var managedPatches = installedPatches
                .Where(patch =>
                    IsManagedResourceOwnershipPatch(patch) ||
                    IsManagedCharacterPresentationPatch(patch) ||
                    IsManagedNodeReadyPresentationPatch(patch))
                .ToArray();
            UnpatchProviderCallbacks(managedPatches);

            var leakedManagedPatches = CaptureProviderPatches(assembly)
                .Where(patch =>
                    IsManagedResourceOwnershipPatch(patch) ||
                    IsManagedCharacterPresentationPatch(patch) ||
                    IsManagedNodeReadyPresentationPatch(patch))
                .ToArray();
            if (leakedManagedPatches.Length > 0)
            {
                throw new InvalidOperationException(
                    $"仍有 {leakedManagedPatches.Length} 个资源/选角呈现补丁未能隔离");
            }

            var behaviorPatches = CaptureProviderPatches(assembly);
            if (!ProviderRuntimeBlueprints.ContainsKey(providerId))
            {
                ProviderRuntimeBlueprints[providerId] = new ProviderRuntimeBlueprint(
                    behaviorPatches,
                    presentationPatches,
                    nodeReadyPresentationPatches);
            }
            else if (skippedInitializer &&
                     ProviderRuntimeBlueprints.TryGetValue(providerId, out var restoredBlueprint))
            {
                // Managed presentation callbacks remain intentionally unpatched and are replayed
                // by Skin Changer against the selected scene when needed.
                presentationPatches = restoredBlueprint.CharacterPresentationPatches.ToArray();
                nodeReadyPresentationPatches = restoredBlueprint.NodeReadyPresentationPatches.ToArray();
            }
            ActiveProviderRuntimes[providerId] = new ActiveProviderRuntime(
                assembly,
                behaviorPatches,
                presentationPatches,
                nodeReadyPresentationPatches);
            ModLog.Info(
                $"已按当前选择启用 {provider.Name} 的完整视觉会话：" +
                $"所需资源已由本 Mod 挂载，{managedPatches.Length} 个资源/选角呈现入口已交由本 Mod 接管，" +
                $"保留 {behaviorPatches.Count} 个原作者动画/场景行为补丁；" +
                "切换离开后会自动卸载行为补丁。");
        }
        catch (Exception exception)
        {
            if (assembly != null)
            {
                UnpatchProviderCallbacks(CaptureProviderPatches(assembly));
            }

            ModLog.Warn(
                $"启用 {provider.Name} 的完整视觉会话失败，已回滚其行为补丁并继续使用资源皮肤：" +
                exception.GetBaseException().Message);
        }
    }

    private static string BuildInitializerKey(
        string providerId,
        Assembly assembly,
        Type initializerType,
        MethodInfo method) =>
        providerId + "|" +
        (assembly.FullName ?? assembly.GetName().Name ?? string.Empty) + "|" +
        initializerType.FullName + "|" +
        method.Name;

    private static void DeactivateProvider(string providerId)
    {
        if (!ActiveProviderRuntimes.Remove(providerId, out var runtime))
        {
            return;
        }

        RestoreNodeReadyBehaviors(runtime);
        RestoreCharacterPresentations(runtime);
        UnpatchProviderCallbacks(runtime.Patches);
        ModLog.Info($"已停用未选中皮肤提供者 {providerId} 的 {runtime.Patches.Count} 个行为补丁。");
    }

    private static IReadOnlyList<ProviderPatch> CaptureProviderPatches(Assembly assembly) =>
        Harmony.GetAllPatchedMethods()
            .SelectMany(target => EnumerateProviderPatches(target, Harmony.GetPatchInfo(target), assembly))
            .DistinctBy(patch => (patch.Target, patch.Callback, patch.Kind))
            .ToArray();

    private static IEnumerable<ProviderPatch> EnumerateProviderPatches(
        MethodBase target,
        Patches? patches,
        Assembly assembly)
    {
        if (patches == null)
        {
            yield break;
        }

        foreach (var entry in patches.Prefixes.Select(patch => (Patch: patch, Kind: ProviderPatchKind.Prefix))
                     .Concat(patches.Postfixes.Select(patch => (Patch: patch, Kind: ProviderPatchKind.Postfix)))
                     .Concat(patches.Transpilers.Select(patch => (Patch: patch, Kind: ProviderPatchKind.Transpiler)))
                     .Concat(patches.Finalizers.Select(patch => (Patch: patch, Kind: ProviderPatchKind.Finalizer))))
        {
            if (ReferenceEquals(entry.Patch.PatchMethod.Module.Assembly, assembly))
            {
                yield return new ProviderPatch(
                    target,
                    entry.Patch.PatchMethod,
                    entry.Kind,
                    entry.Patch.priority,
                    entry.Patch.before,
                    entry.Patch.after,
                    entry.Patch.debug);
            }
        }
    }

    /// <summary>
    /// Skin Changer has already selected and isolated the concrete resource for these targets.
    /// Leaving a provider's own resolver active would run a second selection pipeline before our
    /// final postfix. Besides being redundant, sprite kits may instantiate and harden a very large
    /// scene that is immediately discarded, or return an object from a previously selected skin.
    /// </summary>
    private static bool IsManagedResourceOwnershipPatch(ProviderPatch patch)
    {
        var declaringType = patch.Target.DeclaringType;
        if (declaringType == null)
        {
            return false;
        }

        // A cosmetic provider may patch a rendered relic node before the game has assigned its
        // model. The selected PCK already owns the icon resource, so this eager second texture pass
        // is both redundant and unsafe across game versions.
        if (declaringType.Name.Equals("NRelic", StringComparison.Ordinal) &&
            patch.Target.Name.Equals("_Ready", StringComparison.Ordinal))
        {
            return true;
        }

        if (declaringType == typeof(AssetCache) ||
            declaringType == typeof(ResourceLoader) ||
            declaringType == typeof(AtlasManager) ||
            declaringType == typeof(SceneHelper) ||
            declaringType == typeof(ImageHelper))
        {
            return patch.Target.Name.StartsWith("Get", StringComparison.Ordinal) ||
                   patch.Target.Name.StartsWith("Load", StringComparison.Ordinal) ||
                   patch.Target.Name.StartsWith("Instantiate", StringComparison.Ordinal) ||
                   patch.Target.Name.Contains("Path", StringComparison.OrdinalIgnoreCase);
        }

        if (!typeof(CharacterModel).IsAssignableFrom(declaringType) &&
            !typeof(MonsterModel).IsAssignableFrom(declaringType))
        {
            return false;
        }

        if (patch.Target.Name.Equals(nameof(CharacterModel.CreateVisuals), StringComparison.Ordinal) &&
            (typeof(CharacterModel).IsAssignableFrom(declaringType) ||
             typeof(MonsterModel).IsAssignableFrom(declaringType)))
        {
            return true;
        }

        if (!patch.Target.Name.StartsWith("get_", StringComparison.Ordinal))
        {
            return false;
        }

        var propertyName = patch.Target.Name[4..];
        return new[]
        {
            "Visual", "Scene", "Portrait", "Icon", "Texture", "Background", "MapMarker", "Path"
        }.Any(token => propertyName.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    // Skin Changer owns the character-select baseline.  Every callback kind on these targets
    // must therefore be removed while the provider is selected; otherwise a prefix can mutate
    // the model before our rebuild and a finalizer/transpiler can leak the provider into the next
    // character.  Replay is deliberately limited to postfixes (see the separate predicate).
    private static bool IsManagedCharacterPresentationPatch(ProviderPatch patch) =>
        IsCharacterPresentationTarget(patch.Target);

    private static bool IsReplayableCharacterPresentationPatch(ProviderPatch patch) =>
        patch.Kind == ProviderPatchKind.Postfix &&
        IsCharacterPresentationTarget(patch.Target);

    private static bool IsManagedNodeReadyPresentationPatch(ProviderPatch patch) =>
        IsNodeReadyPresentationTarget(patch.Target);

    private static bool IsNodeReadyPresentationTarget(MethodBase target) =>
        target.Name.Equals("_Ready", StringComparison.Ordinal) &&
        target.DeclaringType != null &&
        (typeof(NCreature).IsAssignableFrom(target.DeclaringType) ||
         typeof(NMerchantRoom).IsAssignableFrom(target.DeclaringType) ||
         typeof(NRestSiteRoom).IsAssignableFrom(target.DeclaringType) ||
         // Merchant skin DLLs usually put their visual override on the button/hand/inventory
         // node itself. Keep these checks name-based so both formal and beta game assemblies
         // remain compatible even when one build moves a merchant node to another namespace.
         target.DeclaringType.Name is
             "NMerchantButton" or
             "NMerchantHand" or
             "NMerchantInventory" or
             "NMerchantSlot" or
             "NMerchantDialogue" or
             "NFakeMerchant");

    /// <summary>
    /// Replays presentation-only postfixes after Skin Changer has rebuilt the current character
    /// selection baseline. This makes behavior-driven skins reversible in both directions without
    /// calling SelectCharacter again (which would replay SFX, screen shake and lobby mutations).
    /// </summary>
    public static void ReplaySelectedCharacterPresentation(
        string providerId,
        NCharacterSelectScreen screen,
        NCharacterSelectButton button,
        CharacterModel character,
        Action? afterReplay = null)
    {
        if (!ActiveProviderRuntimes.TryGetValue(providerId, out var runtime))
        {
            return;
        }

        // Some providers create a separate full-screen layer and hide AnimatedBg instead of
        // replacing the game's character-select scene. Their own "not selected" cleanup cannot
        // run after Skin Changer has isolated the Harmony postfix, so remember exactly what the
        // selected presentation callback changes and undo it before the next replay.
        RestoreCharacterPresentation(runtime, screen);
        var baseline = CaptureCharacterPresentationState(screen);
        var replayed = 0;
        foreach (var patch in runtime.CharacterPresentationPatches)
        {
            var instance = typeof(NCharacterSelectScreen).IsAssignableFrom(patch.Target.DeclaringType)
                ? (object)screen
                : button;
            if (!TryBuildCharacterPresentationArguments(
                    patch.Callback,
                    patch.Target,
                    instance,
                    button,
                    character,
                    out var arguments))
            {
                continue;
            }

            try
            {
                patch.Callback.Invoke(null, arguments);
                replayed++;
            }
            catch (Exception exception)
            {
                ModLog.Warn(
                    $"重放 {providerId} 的选角呈现 {patch.Callback.DeclaringType?.FullName}." +
                    $"{patch.Callback.Name} 失败：{exception.GetBaseException().Message}");
            }
        }

        // Let the caller normalize provider-created controls before recording the final mutation
        // state. This keeps any temporary parent layout change reversible when the skin is left.
        afterReplay?.Invoke();
        TrackCharacterPresentationMutation(runtime, screen, baseline);

        if (replayed > 0)
        {
            ModLog.Info($"已在恢复游戏基线后重放 {providerId} 的 {replayed} 个选角呈现步骤。");
        }
    }

    public static void RestoreCharacterPresentation(NCharacterSelectScreen screen)
    {
        foreach (var runtime in ActiveProviderRuntimes.Values)
        {
            RestoreCharacterPresentation(runtime, screen);
        }
    }

    private static Dictionary<ulong, CharacterPresentationNodeState> CaptureCharacterPresentationState(
        NCharacterSelectScreen screen)
    {
        var result = new Dictionary<ulong, CharacterPresentationNodeState>();
        foreach (var node in EnumerateNodeTree(screen))
        {
            result[node.GetInstanceId()] = new CharacterPresentationNodeState(
                node,
                node is CanvasItem canvasItem ? canvasItem.Visible : null,
                GetCharacterPresentationText(node),
                node is Control control ? control.ClipContents : null);
        }

        return result;
    }

    private static void TrackCharacterPresentationMutation(
        ActiveProviderRuntime runtime,
        NCharacterSelectScreen screen,
        IReadOnlyDictionary<ulong, CharacterPresentationNodeState> baseline)
    {
        var addedRoots = new List<WeakReference<Node>>();
        foreach (var node in EnumerateNodeTree(screen))
        {
            var instanceId = node.GetInstanceId();
            if (baseline.ContainsKey(instanceId))
            {
                continue;
            }

            var parent = node.GetParent();
            if (parent == null || baseline.ContainsKey(parent.GetInstanceId()))
            {
                addedRoots.Add(new WeakReference<Node>(node));
            }
        }

        var visibilityChanges = new List<CharacterPresentationVisibilityChange>();
        var textChanges = new List<CharacterPresentationTextChange>();
        var clipChanges = new List<CharacterPresentationClipChange>();
        foreach (var state in baseline.Values)
        {
            if (!GodotObject.IsInstanceValid(state.Node))
            {
                continue;
            }

            if (state.Visible is { } originalVisibility &&
                state.Node is CanvasItem canvasItem &&
                canvasItem.Visible != originalVisibility)
            {
                visibilityChanges.Add(new CharacterPresentationVisibilityChange(
                    new WeakReference<CanvasItem>(canvasItem),
                    originalVisibility,
                    canvasItem.Visible));
            }

            if (state.Text is { } originalText &&
                GetCharacterPresentationText(state.Node) is { } appliedText &&
                appliedText != originalText)
            {
                textChanges.Add(new CharacterPresentationTextChange(
                    new WeakReference<Node>(state.Node),
                    originalText,
                    appliedText));
            }

            if (state.ClipContents is { } originalClipContents &&
                state.Node is Control control &&
                control.ClipContents != originalClipContents)
            {
                clipChanges.Add(new CharacterPresentationClipChange(
                    new WeakReference<Control>(control),
                    originalClipContents,
                    control.ClipContents));
            }
        }

        if (addedRoots.Count == 0 &&
            visibilityChanges.Count == 0 &&
            textChanges.Count == 0 &&
            clipChanges.Count == 0)
        {
            return;
        }

        runtime.CharacterPresentationMutations[screen.GetInstanceId()] =
            new CharacterPresentationMutation(
                new WeakReference<NCharacterSelectScreen>(screen),
                addedRoots,
                visibilityChanges,
                textChanges,
                clipChanges);
    }

    private static IEnumerable<Node> EnumerateNodeTree(Node root)
    {
        yield return root;
        foreach (var child in root.GetChildren())
        {
            foreach (var descendant in EnumerateNodeTree(child))
            {
                yield return descendant;
            }
        }
    }

    private static void RestoreCharacterPresentations(ActiveProviderRuntime runtime)
    {
        foreach (var mutation in runtime.CharacterPresentationMutations.Values.ToArray())
        {
            RestoreCharacterPresentationMutation(mutation);
        }

        runtime.CharacterPresentationMutations.Clear();
    }

    private static void RestoreCharacterPresentation(
        ActiveProviderRuntime runtime,
        NCharacterSelectScreen screen)
    {
        var instanceId = screen.GetInstanceId();
        if (!runtime.CharacterPresentationMutations.Remove(instanceId, out var mutation))
        {
            return;
        }

        if (!mutation.Screen.TryGetTarget(out var trackedScreen) ||
            !ReferenceEquals(trackedScreen, screen))
        {
            return;
        }

        RestoreCharacterPresentationMutation(mutation);
    }

    private static void RestoreCharacterPresentationMutation(CharacterPresentationMutation mutation)
    {
        foreach (var addedNodeReference in mutation.AddedRoots)
        {
            if (!addedNodeReference.TryGetTarget(out var addedNode) ||
                !GodotObject.IsInstanceValid(addedNode))
            {
                continue;
            }

            var parent = addedNode.GetParent();
            if (parent != null && GodotObject.IsInstanceValid(parent))
            {
                parent.RemoveChildSafely(addedNode);
            }

            addedNode.QueueFreeSafely();
        }

        foreach (var visibilityChange in mutation.VisibilityChanges)
        {
            if (visibilityChange.Node.TryGetTarget(out var canvasItem) &&
                GodotObject.IsInstanceValid(canvasItem))
            {
                // Preserve a later game/UI update if it replaced the provider's value while the
                // presentation was active. Only undo the exact value captured from this replay.
                if (canvasItem.Visible == visibilityChange.AppliedVisibility)
                {
                    canvasItem.Visible = visibilityChange.OriginalVisibility;
                }
            }
        }

        foreach (var textChange in mutation.TextChanges)
        {
            if (!textChange.Node.TryGetTarget(out var node) ||
                !GodotObject.IsInstanceValid(node) ||
                GetCharacterPresentationText(node) != textChange.AppliedText)
            {
                continue;
            }

            SetCharacterPresentationText(node, textChange.OriginalText);
        }

        foreach (var clipChange in mutation.ClipChanges)
        {
            if (clipChange.Node.TryGetTarget(out var control) &&
                GodotObject.IsInstanceValid(control) &&
                control.ClipContents == clipChange.AppliedClipContents)
            {
                control.ClipContents = clipChange.OriginalClipContents;
            }
        }
    }

    private static string? GetCharacterPresentationText(Node node) =>
        node switch
        {
            Label label => label.Text,
            RichTextLabel richTextLabel => richTextLabel.Text,
            _ => null
        };

    private static void SetCharacterPresentationText(Node node, string text)
    {
        switch (node)
        {
            case Label label:
                label.Text = text;
                break;
            case RichTextLabel richTextLabel:
                richTextLabel.Text = text;
                break;
        }
    }

    /// <summary>
    /// A few complete character skins attach their rendered actor or companion from an NCreature._Ready postfix
    /// instead of returning it from CharacterModel.CreateVisuals. Live replacement creates a fresh visuals tree but
    /// deliberately does not call NCreature._Ready again, because the game's own method would duplicate combat state,
    /// signals, health bars and orb managers. Replay only the selected provider's parameter-compatible postfixes so
    /// those cosmetic children receive the same setup as a creature created after the skin was selected.
    /// </summary>
    public static void ReplaySelectedCreatureReady(string providerId, NCreature creature)
    {
        if (!ActiveProviderRuntimes.TryGetValue(providerId, out var runtime))
        {
            return;
        }

        var room = NCombatRoom.Instance;
        var replayed = 0;
        foreach (var patch in runtime.Patches.Where(patch =>
                     patch.Kind == ProviderPatchKind.Postfix && IsLiveCreatureInitializationPatch(
                         patch.Target,
                         creature,
                         room)))
        {
            if (!TryBuildCreatureInitializationArguments(
                    patch.Callback,
                    patch.Target,
                    creature,
                    room,
                    out var arguments))
            {
                continue;
            }

            try
            {
                patch.Callback.Invoke(null, arguments);
                replayed++;
            }
            catch (Exception exception)
            {
                ModLog.Warn(
                    $"重放 {providerId} 的实战生物初始化 {patch.Callback.DeclaringType?.FullName}." +
                    $"{patch.Callback.Name} 失败：{exception.GetBaseException().Message}");
            }
        }

        if (replayed > 0)
        {
            ModLog.Info($"已为 {creature.Entity.ModelId.Entry} 重放 {providerId} 的 {replayed} 个实战生物初始化步骤。");
        }
    }

    /// <summary>
    /// Replays parameter-free visual setup attached to a Godot node after a provider is hot-selected
    /// or when a managed per-owner node reaches _Ready. The node is snapshotted before the replay,
    /// so added children and common CanvasItem/Node2D/Control mutations can be restored when the
    /// provider is deselected, replaced for this owner, or replayed again.
    /// </summary>
    public static IReadOnlyList<Node> ReplaySelectedNodeReadyBehavior(
        string providerId,
        Node node,
        bool includePrefixes = false)
    {
        if (!ActiveProviderRuntimes.TryGetValue(providerId, out var runtime) ||
            !GodotObject.IsInstanceValid(node))
        {
            return [];
        }

        BeginSelectedNodeReadyTracking(providerId, node);
        var replayed = 0;
        try
        {
            replayed = InvokeSelectedNodeReadyCallbacks(
                providerId,
                runtime,
                node,
                includePrefixes,
                includePostfixes: true);
        }
        finally
        {
            EndSelectedNodeReadyTracking(providerId, node);
        }

        var addedRoots = GetTrackedNodeReadyAdditions(runtime, node);
        if (replayed > 0)
        {
            ModLog.Info($"已为现有场景节点重放 {providerId} 的 {replayed} 个外观初始化步骤。");
        }

        return addedRoots;
    }

    /// <summary>
    /// Runs an isolated provider's visual _Ready prefixes before the game's original _Ready.
    /// Merchant providers use this phase to replace the skeleton resource that the original
    /// button/hand then binds to. Replaying the same prefix after _Ready leaves the game holding
    /// a stale MegaSkeleton and breaks hover outlines and hand variants.
    /// </summary>
    public static IReadOnlyList<Node> ReplaySelectedNodeReadyPrefixes(
        string providerId,
        Node node)
    {
        if (!ActiveProviderRuntimes.TryGetValue(providerId, out var runtime) ||
            !GodotObject.IsInstanceValid(node))
        {
            return [];
        }

        var nodeId = node.GetInstanceId();
        RestoreNodeReadyMutation(runtime, nodeId);
        var baseline = new NodeReadyBaseline(
            new WeakReference<Node>(node),
            CaptureNodeReadyState(node));
        var replayed = InvokeSelectedNodeReadyCallbacks(
            providerId,
            runtime,
            node,
            includePrefixes: true,
            includePostfixes: false);
        var mutation = BuildNodeReadyMutation(node, baseline);
        if (mutation != null)
        {
            runtime.NodeReadyMutations[nodeId] = mutation;
        }

        if (replayed > 0)
        {
            ModLog.Info($"已在原生 _Ready 前重放 {providerId} 的 {replayed} 个外观初始化步骤。");
        }

        return GetTrackedNodeReadyAdditions(runtime, node);
    }

    /// <summary>
    /// Completes a split native _Ready replay after the game's original method has initialized
    /// its fields and signals. Prefix and postfix mutations are merged in reverse application
    /// order so deselection can restore both without undoing vanilla _Ready state.
    /// </summary>
    public static IReadOnlyList<Node> ReplaySelectedNodeReadyPostfixes(
        string providerId,
        Node node)
    {
        if (!ActiveProviderRuntimes.TryGetValue(providerId, out var runtime) ||
            !GodotObject.IsInstanceValid(node))
        {
            return [];
        }

        var nodeId = node.GetInstanceId();
        var baseline = new NodeReadyBaseline(
            new WeakReference<Node>(node),
            CaptureNodeReadyState(node));
        var replayed = InvokeSelectedNodeReadyCallbacks(
            providerId,
            runtime,
            node,
            includePrefixes: false,
            includePostfixes: true);
        var postfixMutation = BuildNodeReadyMutation(node, baseline);
        if (postfixMutation != null)
        {
            runtime.NodeReadyMutations[nodeId] = runtime.NodeReadyMutations.TryGetValue(
                    nodeId,
                    out var prefixMutation)
                ? MergeNodeReadyMutations(prefixMutation, postfixMutation)
                : postfixMutation;
        }

        if (replayed > 0)
        {
            ModLog.Info($"已在原生 _Ready 后重放 {providerId} 的 {replayed} 个外观初始化步骤。");
        }

        return GetTrackedNodeReadyAdditions(runtime, node);
    }

    public static void RestoreSelectedNodeReadyBehavior(string providerId, Node node)
    {
        if (ActiveProviderRuntimes.TryGetValue(providerId, out var runtime) &&
            GodotObject.IsInstanceValid(node))
        {
            RestoreNodeReadyMutation(runtime, node.GetInstanceId());
        }
    }

    private static int InvokeSelectedNodeReadyCallbacks(
        string providerId,
        ActiveProviderRuntime runtime,
        Node node,
        bool includePrefixes,
        bool includePostfixes)
    {
        var replayed = 0;
        foreach (var patch in runtime.Patches
                     .Concat(runtime.NodeReadyPresentationPatches)
                     .Where(patch =>
                         ((includePrefixes && patch.Kind == ProviderPatchKind.Prefix) ||
                          (includePostfixes && patch.Kind == ProviderPatchKind.Postfix)) &&
                         patch.Target.Name.Equals("_Ready", StringComparison.Ordinal) &&
                         patch.Target.DeclaringType?.IsInstanceOfType(node) == true)
                     .DistinctBy(patch => (patch.Target, patch.Callback, patch.Kind)))
        {
            if (!TryBuildNodeReadyArguments(
                    patch.Callback,
                    patch.Target,
                    node,
                    out var arguments))
            {
                continue;
            }

            try
            {
                patch.Callback.Invoke(null, arguments);
                replayed++;
            }
            catch (Exception exception)
            {
                ModLog.Warn(
                    $"重放 {providerId} 的场景外观初始化 " +
                    $"{patch.Callback.DeclaringType?.FullName}.{patch.Callback.Name} 失败：" +
                    exception.GetBaseException().Message);
            }
        }

        return replayed;
    }

    private static IReadOnlyList<Node> GetTrackedNodeReadyAdditions(
        ActiveProviderRuntime runtime,
        Node node) =>
        runtime.NodeReadyMutations.TryGetValue(node.GetInstanceId(), out var mutation)
            ? mutation.AddedRoots
                .Select(reference => reference.TryGetTarget(out var addedNode) ? addedNode : null)
                .Where(addedNode => addedNode != null && GodotObject.IsInstanceValid(addedNode))
                .Cast<Node>()
                .ToArray()
            : [];

    private static NodeReadyMutation MergeNodeReadyMutations(
        NodeReadyMutation prefix,
        NodeReadyMutation postfix)
    {
        var added = postfix.AddedRoots
            .Concat(prefix.AddedRoots)
            .Where(reference => reference.TryGetTarget(out var node) &&
                                GodotObject.IsInstanceValid(node))
            .DistinctBy(reference =>
                reference.TryGetTarget(out var node) ? node.GetInstanceId() : 0UL)
            .ToArray();
        // Restore the latest changes first. If both phases touched the same property, the
        // postfix returns it to the prefix-applied value and the prefix then returns the true
        // pre-provider value.
        var changes = postfix.Changes.Concat(prefix.Changes).ToArray();
        return new NodeReadyMutation(added, changes);
    }

    /// <summary>
    /// Replays the isolated room-level visual postfixes for every currently selected full runtime
    /// provider. Room _Ready has already run by the time a hot appearance switch is requested, so
    /// invoking these callbacks on the live room is the safe equivalent of rebuilding the room.
    /// Each provider is tracked independently; leaving it restores only nodes and properties that
    /// its callback actually introduced.
    /// </summary>
    public static IReadOnlyList<(string ProviderId, IReadOnlyList<Node> AddedRoots)>
        ReplaySelectedRoomReadyBehaviors(Node room, string? onlyProviderId = null)
    {
        if (!GodotObject.IsInstanceValid(room))
        {
            return [];
        }

        var results = new List<(string ProviderId, IReadOnlyList<Node> AddedRoots)>();
        foreach (var providerId in ActiveProviderRuntimes
                     .Where(pair => pair.Value.NodeReadyPresentationPatches.Any(patch =>
                         patch.Target.DeclaringType?.IsInstanceOfType(room) == true) &&
                         (onlyProviderId == null ||
                          pair.Key.Equals(onlyProviderId, StringComparison.OrdinalIgnoreCase)))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            var roots = ReplaySelectedNodeReadyBehavior(providerId, room);
            results.Add((providerId, roots));
        }

        return results;
    }

    /// <summary>
    /// Starts tracking a provider's visual changes to an already existing node. This is also
    /// used by the room lifecycle patches: a provider may have run its _Ready postfix normally
    /// before the appearance panel is opened, so cleanup must cover that first invocation too.
    /// </summary>
    public static void BeginSelectedNodeReadyTracking(string providerId, Node node)
    {
        if (!ActiveProviderRuntimes.TryGetValue(providerId, out var runtime) ||
            !GodotObject.IsInstanceValid(node))
        {
            return;
        }

        var nodeId = node.GetInstanceId();
        RestoreNodeReadyMutation(runtime, nodeId);
        runtime.PendingNodeReadyBaselines[nodeId] = new NodeReadyBaseline(
            new WeakReference<Node>(node),
            CaptureNodeReadyState(node));
    }

    /// <summary>
    /// Finishes a node tracking scope and stores only the mutations introduced by the selected
    /// provider. Returning no value keeps the API safe for Harmony Prefix/Postfix callers; the
    /// replay method reads the stored weak references when it needs the newly added roots.
    /// </summary>
    public static void EndSelectedNodeReadyTracking(string providerId, Node node)
    {
        if (!ActiveProviderRuntimes.TryGetValue(providerId, out var runtime) ||
            !GodotObject.IsInstanceValid(node) ||
            !runtime.PendingNodeReadyBaselines.Remove(node.GetInstanceId(), out var baseline))
        {
            return;
        }

        var mutation = BuildNodeReadyMutation(node, baseline);
        if (mutation != null)
        {
            runtime.NodeReadyMutations[node.GetInstanceId()] = mutation;
        }
    }

    /// <summary>
    /// Removes node-ready presentation left on one live scene instance by providers that are not
    /// selected for that instance. Providers can remain active because another multiplayer owner
    /// still uses them, so global provider deactivation is not sufficient for per-player cleanup.
    /// </summary>
    public static void RestoreUnselectedNodeReadyBehaviors(
        Node node,
        IEnumerable<string> selectedProviderIds)
    {
        if (!GodotObject.IsInstanceValid(node))
        {
            return;
        }

        var selected = selectedProviderIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nodeId = node.GetInstanceId();
        foreach (var pair in ActiveProviderRuntimes.ToArray())
        {
            if (!selected.Contains(pair.Key))
            {
                RestoreNodeReadyMutation(pair.Value, nodeId);
            }
        }
    }

    private static NodeReadyMutation? BuildNodeReadyMutation(
        Node node,
        NodeReadyBaseline baseline)
    {
        var baselineIds = baseline.States.Keys.ToHashSet();
        var addedRoots = EnumerateNodeTree(node)
            .Where(candidate => !baselineIds.Contains(candidate.GetInstanceId()))
            .Where(candidate => candidate.GetParent() is not { } parent ||
                                baselineIds.Contains(parent.GetInstanceId()))
            .Select(candidate => new WeakReference<Node>(candidate))
            .ToArray();
        // Capture the live tree once. The previous implementation captured the entire tree once
        // per baseline node, which made a large shop scene quadratic and caused visible pauses on
        // every hot switch.
        var currentStates = CaptureNodeReadyState(node);
        var changes = new List<NodeReadyVisualChange>();
        foreach (var state in baseline.States.Values)
        {
            if (!state.Node.TryGetTarget(out var candidate) ||
                !GodotObject.IsInstanceValid(candidate))
            {
                continue;
            }

            if (!currentStates.TryGetValue(candidate.GetInstanceId(), out var current))
            {
                continue;
            }

            if (!HasNodeReadyVisualChanged(state, current))
            {
                continue;
            }

            changes.Add(new NodeReadyVisualChange(state.Node, state, current));
        }

        return addedRoots.Length == 0 && changes.Count == 0
            ? null
            : new NodeReadyMutation(addedRoots, changes);
    }

    private static bool HasNodeReadyVisualChanged(
        NodeReadyVisualState baseline,
        NodeReadyVisualState current) =>
        baseline.Visible != current.Visible ||
        baseline.Modulate != current.Modulate ||
        baseline.SelfModulate != current.SelfModulate ||
        baseline.ZIndex != current.ZIndex ||
        baseline.ZAsRelative != current.ZAsRelative ||
        baseline.Node2DPosition != current.Node2DPosition ||
        baseline.Node2DScale != current.Node2DScale ||
        baseline.Node2DRotation != current.Node2DRotation ||
        baseline.ControlPosition != current.ControlPosition ||
        baseline.ControlSize != current.ControlSize ||
        baseline.ControlScale != current.ControlScale ||
        baseline.ControlRotation != current.ControlRotation ||
        baseline.ControlPivotOffset != current.ControlPivotOffset ||
        baseline.ControlMouseFilter != current.ControlMouseFilter;

    private static Dictionary<ulong, NodeReadyVisualState> CaptureNodeReadyState(Node root)
    {
        var result = new Dictionary<ulong, NodeReadyVisualState>();
        foreach (var node in EnumerateNodeTree(root))
        {
            if (!GodotObject.IsInstanceValid(node))
            {
                continue;
            }

            var canvas = node as CanvasItem;
            var node2d = node as Node2D;
            var control = node as Control;
            result[node.GetInstanceId()] = new NodeReadyVisualState(
                new WeakReference<Node>(node),
                canvas?.Visible,
                canvas?.Modulate,
                canvas?.SelfModulate,
                canvas?.ZIndex,
                canvas?.ZAsRelative,
                node2d?.Position,
                node2d?.Scale,
                node2d?.Rotation,
                control?.Position,
                control?.Size,
                control?.Scale,
                control?.Rotation,
                control?.PivotOffset,
                control?.MouseFilter);
        }

        return result;
    }

    private static void RestoreNodeReadyBehaviors(ActiveProviderRuntime runtime)
    {
        foreach (var mutation in runtime.NodeReadyMutations.Values.ToArray())
        {
            RestoreNodeReadyMutation(mutation);
        }

        runtime.NodeReadyMutations.Clear();
        runtime.PendingNodeReadyBaselines.Clear();
    }

    private static void RestoreNodeReadyMutation(
        ActiveProviderRuntime runtime,
        ulong nodeId)
    {
        if (runtime.NodeReadyMutations.Remove(nodeId, out var mutation))
        {
            RestoreNodeReadyMutation(mutation);
        }

        runtime.PendingNodeReadyBaselines.Remove(nodeId);
    }

    private static void RestoreNodeReadyMutation(NodeReadyMutation mutation)
    {
        foreach (var addedReference in mutation.AddedRoots)
        {
            if (!addedReference.TryGetTarget(out var node) ||
                !GodotObject.IsInstanceValid(node))
            {
                continue;
            }

            node.GetParent()?.RemoveChildSafely(node);
            node.QueueFreeSafely();
        }

        foreach (var change in mutation.Changes)
        {
            if (!change.Node.TryGetTarget(out var node) ||
                !GodotObject.IsInstanceValid(node))
            {
                continue;
            }

            var original = change.Original;
            var applied = change.Applied;
            if (node is CanvasItem canvas)
            {
                if (original.Visible is { } visible &&
                    applied.Visible is { } appliedVisible &&
                    canvas.Visible == appliedVisible)
                {
                    canvas.Visible = visible;
                }

                if (original.Modulate is { } modulate &&
                    applied.Modulate is { } appliedModulate &&
                    canvas.Modulate == appliedModulate)
                {
                    canvas.Modulate = modulate;
                }

                if (original.SelfModulate is { } selfModulate &&
                    applied.SelfModulate is { } appliedSelfModulate &&
                    canvas.SelfModulate == appliedSelfModulate)
                {
                    canvas.SelfModulate = selfModulate;
                }

                if (original.ZIndex is { } zIndex &&
                    applied.ZIndex is { } appliedZIndex &&
                    canvas.ZIndex == appliedZIndex)
                {
                    canvas.ZIndex = zIndex;
                }

                if (original.ZAsRelative is { } zAsRelative &&
                    applied.ZAsRelative is { } appliedZAsRelative &&
                    canvas.ZAsRelative == appliedZAsRelative)
                {
                    canvas.ZAsRelative = zAsRelative;
                }
            }

            if (node is Node2D node2d)
            {
                if (original.Node2DPosition is { } position &&
                    applied.Node2DPosition is { } appliedPosition &&
                    node2d.Position == appliedPosition)
                {
                    node2d.Position = position;
                }

                if (original.Node2DScale is { } scale &&
                    applied.Node2DScale is { } appliedScale &&
                    node2d.Scale == appliedScale)
                {
                    node2d.Scale = scale;
                }

                if (original.Node2DRotation is { } rotation &&
                    applied.Node2DRotation is { } appliedRotation &&
                    Mathf.IsEqualApprox(node2d.Rotation, appliedRotation))
                {
                    node2d.Rotation = rotation;
                }
            }

            if (node is Control control)
            {
                if (original.ControlPosition is { } position &&
                    applied.ControlPosition is { } appliedPosition &&
                    control.Position == appliedPosition)
                {
                    control.Position = position;
                }

                if (original.ControlSize is { } size &&
                    applied.ControlSize is { } appliedSize &&
                    control.Size == appliedSize)
                {
                    control.Size = size;
                }

                if (original.ControlScale is { } scale &&
                    applied.ControlScale is { } appliedScale &&
                    control.Scale == appliedScale)
                {
                    control.Scale = scale;
                }

                if (original.ControlRotation is { } rotation &&
                    applied.ControlRotation is { } appliedRotation &&
                    Mathf.IsEqualApprox(control.Rotation, appliedRotation))
                {
                    control.Rotation = rotation;
                }

                if (original.ControlPivotOffset is { } pivotOffset &&
                    applied.ControlPivotOffset is { } appliedPivotOffset &&
                    control.PivotOffset == appliedPivotOffset)
                {
                    control.PivotOffset = pivotOffset;
                }

                if (original.ControlMouseFilter is { } mouseFilter &&
                    applied.ControlMouseFilter is { } appliedMouseFilter &&
                    control.MouseFilter == appliedMouseFilter)
                {
                    control.MouseFilter = mouseFilter;
                }
            }
        }
    }

    private static bool IsLiveCreatureInitializationPatch(
        MethodBase target,
        NCreature creature,
        NCombatRoom? room)
    {
        if (target.Name.Equals(nameof(NCreature._Ready), StringComparison.Ordinal) &&
            target.DeclaringType?.IsInstanceOfType(creature) == true)
        {
            return true;
        }

        // Full presentation packs sometimes add attachment markers, prewarm their VFX, or bind
        // auxiliary visuals from NCombatRoom.AddCreature instead of NCreature._Ready. A hot swap
        // does not add the Creature model again, so replay only compatible postfixes after the new
        // NCreatureVisuals has been installed. Unsupported callbacks are skipped by the argument
        // builder below rather than being invoked with fabricated state.
        return room != null &&
               target.Name.Equals("AddCreature", StringComparison.Ordinal) &&
               target.DeclaringType?.IsInstanceOfType(room) == true;
    }

    private static bool IsCharacterPresentationTarget(MethodBase target) =>
        target.DeclaringType != null &&
        ((typeof(NCharacterSelectScreen).IsAssignableFrom(target.DeclaringType) &&
          target.Name.Equals(nameof(NCharacterSelectScreen.SelectCharacter), StringComparison.Ordinal)) ||
         (typeof(NCharacterSelectButton).IsAssignableFrom(target.DeclaringType) &&
          target.Name.Equals("Init", StringComparison.Ordinal)));

    private static bool TryBuildCharacterPresentationArguments(
        MethodInfo callback,
        MethodBase target,
        object instance,
        NCharacterSelectButton button,
        CharacterModel character,
        out object?[] arguments)
    {
        var parameters = callback.GetParameters();
        arguments = new object?[parameters.Length];
        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            var parameterType = parameter.ParameterType.IsByRef
                ? parameter.ParameterType.GetElementType()!
                : parameter.ParameterType;
            switch (parameter.Name)
            {
                case "__instance" when parameterType.IsInstanceOfType(instance):
                    arguments[index] = instance;
                    break;
                case "__originalMethod" when parameterType == typeof(MethodBase):
                    arguments[index] = target;
                    break;
                case "__runOriginal" when parameterType == typeof(bool):
                    arguments[index] = true;
                    break;
                // Harmony injects a fresh state value for a postfix paired with a prefix.  The
                // prefix is intentionally isolated, so replay with the neutral value instead of
                // refusing the callback altogether.  This still lets the visual part of a
                // postfix (custom scene/title/avatar) run without restoring audio or other
                // transient state owned by the original prefix.
                case "__state":
                    arguments[index] = parameterType.IsValueType
                        ? Activator.CreateInstance(parameterType)
                        : null;
                    break;
                case "charSelectButton" or "button" when parameterType.IsInstanceOfType(button):
                    arguments[index] = button;
                    break;
                case "characterModel" or "character" or "model" when parameterType.IsInstanceOfType(character):
                    arguments[index] = character;
                    break;
                default:
                    if (parameter.HasDefaultValue)
                    {
                        arguments[index] = parameter.DefaultValue;
                        break;
                    }

                    return false;
            }
        }

        return true;
    }

    private static bool TryBuildCreatureInitializationArguments(
        MethodInfo callback,
        MethodBase target,
        NCreature creature,
        NCombatRoom? room,
        out object?[] arguments)
    {
        var parameters = callback.GetParameters();
        arguments = new object?[parameters.Length];
        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            var parameterType = parameter.ParameterType.IsByRef
                ? parameter.ParameterType.GetElementType()!
                : parameter.ParameterType;
            switch (parameter.Name)
            {
                case "__instance" when parameterType.IsInstanceOfType(creature):
                    arguments[index] = creature;
                    break;
                case "__instance" when room != null && parameterType.IsInstanceOfType(room):
                    arguments[index] = room;
                    break;
                case "creature" when parameterType.IsInstanceOfType(creature.Entity):
                    arguments[index] = creature.Entity;
                    break;
                case "__originalMethod" when parameterType == typeof(MethodBase):
                    arguments[index] = target;
                    break;
                case "__runOriginal" when parameterType == typeof(bool):
                    arguments[index] = true;
                    break;
                case "__state":
                    arguments[index] = parameterType.IsValueType
                        ? Activator.CreateInstance(parameterType)
                        : null;
                    break;
                default:
                    if (parameter.HasDefaultValue)
                    {
                        arguments[index] = parameter.DefaultValue;
                        break;
                    }

                    return false;
            }
        }

        return true;
    }

    private static bool TryBuildNodeReadyArguments(
        MethodInfo callback,
        MethodBase target,
        Node node,
        out object?[] arguments)
    {
        var parameters = callback.GetParameters();
        arguments = new object?[parameters.Length];
        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            var parameterType = parameter.ParameterType.IsByRef
                ? parameter.ParameterType.GetElementType()!
                : parameter.ParameterType;
            switch (parameter.Name)
            {
                case "__instance" when parameterType.IsInstanceOfType(node):
                    arguments[index] = node;
                    break;
                case "__originalMethod" when parameterType == typeof(MethodBase):
                    arguments[index] = target;
                    break;
                case "__runOriginal" when parameterType == typeof(bool):
                    arguments[index] = true;
                    break;
                case "__state":
                    arguments[index] = parameterType.IsValueType
                        ? Activator.CreateInstance(parameterType)
                        : null;
                    break;
                default:
                    if (parameter.HasDefaultValue)
                    {
                        arguments[index] = parameter.DefaultValue;
                        break;
                    }

                    return false;
            }
        }

        return true;
    }

    private static void UnpatchProviderCallbacks(IEnumerable<ProviderPatch> patches)
    {
        var harmony = new Harmony(Entry.ModId + ".provider_runtime");
        foreach (var patch in patches)
        {
            try
            {
                harmony.Unpatch(patch.Target, patch.Callback);
            }
            catch (Exception exception)
            {
                ModLog.Warn(
                    $"卸载提供者行为补丁 {patch.Callback.DeclaringType?.FullName}.{patch.Callback.Name} 失败：" +
                    exception.GetBaseException().Message);
            }
        }
    }

    private static void PatchProviderCallbacks(IEnumerable<ProviderPatch> patches)
    {
        var harmony = new Harmony(Entry.ModId + ".provider_runtime");
        foreach (var patch in patches)
        {
            try
            {
                var method = new HarmonyMethod(
                    patch.Callback,
                    patch.Priority,
                    patch.Before,
                    patch.After,
                    patch.Debug);
                _ = patch.Kind switch
                {
                    ProviderPatchKind.Prefix => harmony.Patch(patch.Target, prefix: method),
                    ProviderPatchKind.Postfix => harmony.Patch(patch.Target, postfix: method),
                    ProviderPatchKind.Transpiler => harmony.Patch(patch.Target, transpiler: method),
                    ProviderPatchKind.Finalizer => harmony.Patch(patch.Target, finalizer: method),
                    _ => throw new ArgumentOutOfRangeException(nameof(patch.Kind), patch.Kind, null)
                };
            }
            catch (Exception exception)
            {
                ModLog.Warn(
                    $"恢复提供者行为补丁 {patch.Callback.DeclaringType?.FullName}.{patch.Callback.Name} 失败：" +
                    exception.GetBaseException().Message);
            }
        }
    }

    private static string NormalizeHarmonyId(string providerId) =>
        new(providerId.Select(character =>
            char.IsLetterOrDigit(character) || character is '.' or '-' or '_'
                ? character
                : '_').ToArray());

    /// <summary>
    /// Runs only the selected provider's Harmony postfixes for model visual creation when the
    /// provider is not eligible for a full selected runtime. This preserves isolation for
    /// multi-group providers while retaining transforms and scene finishing work that cannot be
    /// represented by replacement textures alone.
    /// </summary>
    public static void ApplySelectedVisualPostfix(
        string providerId,
        object model,
        ref NCreatureVisuals visuals)
    {
        if (!ProviderAssemblies.TryGetValue(providerId, out var provider) ||
            !GodotObject.IsInstanceValid(visuals))
        {
            return;
        }

        try
        {
            var assembly = GetOrLoadProviderAssembly(provider);
            if (assembly == null)
            {
                return;
            }

            if (!VisualPostfixesByProvider.TryGetValue(providerId, out var postfixes))
            {
                postfixes = DiscoverVisualPostfixes(assembly);
                VisualPostfixesByProvider[providerId] = postfixes;
            }

            foreach (var postfix in postfixes.Where(candidate =>
                         candidate.TargetType.IsInstanceOfType(model)))
            {
                InvokeVisualPostfix(provider, postfix, model, ref visuals);
            }
        }
        catch (Exception exception)
        {
            var key = providerId + ":discovery";
            if (FailedVisualPostfixes.Add(key))
            {
                ModLog.Warn(
                    $"读取 {provider.Name} 的按需视觉后处理失败，已继续使用资源皮肤：" +
                    exception.GetBaseException().Message);
            }
        }
    }

    private static Assembly? GetOrLoadProviderAssembly(ProviderAssembly provider)
    {
        if (LoadedProviderAssemblies.TryGetValue(provider.AssemblyPath, out var loadedProviderAssembly))
        {
            return loadedProviderAssembly;
        }

        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(candidate =>
            {
                try
                {
                    return !candidate.IsDynamic &&
                           Path.GetFullPath(candidate.Location)
                               .Equals(provider.AssemblyPath, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            });
        if (assembly != null)
        {
            LoadedProviderAssemblies[provider.AssemblyPath] = assembly;
            return assembly;
        }

        var loadContext = AssemblyLoadContext.GetLoadContext(Assembly.GetExecutingAssembly());
        if (ProviderAssemblyCompatibility.TryRewriteForCurrentGame(
                provider.AssemblyPath,
                out var rewrittenAssembly,
                out var rewrittenCalls,
                out var compatibilityFailure))
        {
            using (rewrittenAssembly)
            {
                assembly = loadContext?.LoadFromStream(rewrittenAssembly!) ??
                           Assembly.Load(rewrittenAssembly!.ToArray());
            }

            LoadedProviderAssemblies[provider.AssemblyPath] = assembly;
            ModLog.Info(
                $"已为 {provider.Name} 桥接 {rewrittenCalls} 处跨游戏版本运行时接口调用。" +
                "该处理按接口签名识别，不依赖皮肤 Mod 名称。");
            return assembly;
        }

        if (!string.IsNullOrWhiteSpace(compatibilityFailure))
        {
            ModLog.Warn($"检查 {provider.Name} 的跨版本运行时接口失败：{compatibilityFailure}");
        }

        assembly = loadContext?.LoadFromAssemblyPath(provider.AssemblyPath) ??
                   Assembly.LoadFrom(provider.AssemblyPath);
        LoadedProviderAssemblies[provider.AssemblyPath] = assembly;
        return assembly;
    }

    private static IReadOnlyList<ManagedVisualPostfix> DiscoverVisualPostfixes(Assembly assembly)
    {
        return GetLoadableTypes(assembly)
            .Select(type => (Type: type, Target: GetVisualPatchTarget(type)))
            .Where(pair => pair.Target != null)
            .SelectMany(pair => pair.Type
                .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(IsPostfixMethod)
                .Select(method => new ManagedVisualPostfix(pair.Target!, method)))
            .OrderBy(postfix => postfix.Method.MetadataToken)
            .ToArray();
    }

    private static Type? GetVisualPatchTarget(Type patchType)
    {
        Type? targetType = null;
        string? methodName = null;
        foreach (var attribute in patchType.CustomAttributes.Where(attribute =>
                     attribute.AttributeType.FullName == "HarmonyLib.HarmonyPatch"))
        {
            foreach (var argument in attribute.ConstructorArguments)
            {
                if (argument.ArgumentType == typeof(Type))
                {
                    targetType = argument.Value as Type;
                }
                else if (argument.ArgumentType == typeof(string))
                {
                    methodName = argument.Value as string;
                }
            }

            foreach (var argument in attribute.NamedArguments)
            {
                if (argument.TypedValue.ArgumentType == typeof(Type))
                {
                    targetType = argument.TypedValue.Value as Type;
                }
                else if (argument.TypedValue.ArgumentType == typeof(string))
                {
                    methodName = argument.TypedValue.Value as string;
                }
            }
        }

        if (!string.Equals(methodName, nameof(CharacterModel.CreateVisuals), StringComparison.Ordinal) ||
            targetType == null ||
            (!typeof(CharacterModel).IsAssignableFrom(targetType) &&
             !typeof(MonsterModel).IsAssignableFrom(targetType)))
        {
            return null;
        }

        return targetType;
    }

    private static bool IsPostfixMethod(MethodInfo method) =>
        method.Name.Equals("Postfix", StringComparison.Ordinal) ||
        method.CustomAttributes.Any(attribute =>
            attribute.AttributeType.FullName == "HarmonyLib.HarmonyPostfix");

    private static void InvokeVisualPostfix(
        ProviderAssembly provider,
        ManagedVisualPostfix postfix,
        object model,
        ref NCreatureVisuals visuals)
    {
        var parameters = postfix.Method.GetParameters();
        var arguments = new object?[parameters.Length];
        var resultIndex = -1;
        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            var parameterType = parameter.ParameterType.IsByRef
                ? parameter.ParameterType.GetElementType()!
                : parameter.ParameterType;
            switch (parameter.Name)
            {
                case "__instance" when parameterType.IsInstanceOfType(model):
                    arguments[index] = model;
                    break;
                case "__result" when parameterType.IsAssignableFrom(typeof(NCreatureVisuals)) ||
                                      typeof(NCreatureVisuals).IsAssignableFrom(parameterType):
                    arguments[index] = visuals;
                    resultIndex = index;
                    break;
                case "__originalMethod" when parameterType == typeof(MethodBase):
                    arguments[index] = AccessTools.Method(postfix.TargetType, nameof(CharacterModel.CreateVisuals));
                    break;
                case "__runOriginal" when parameterType == typeof(bool):
                    arguments[index] = true;
                    break;
                default:
                    if (parameter.HasDefaultValue)
                    {
                        arguments[index] = parameter.DefaultValue;
                        break;
                    }

                    return;
            }
        }

        var key = provider.AssemblyPath + ":" + postfix.Method.MetadataToken;
        try
        {
            var spineSnapshot = CaptureAliasedSpineResource(visuals);
            var returned = postfix.Method.Invoke(null, arguments);
            if (resultIndex >= 0 && arguments[resultIndex] is NCreatureVisuals replaced)
            {
                visuals = replaced;
            }
            else if (returned is NCreatureVisuals returnedVisuals)
            {
                visuals = returnedVisuals;
            }

            RestoreAliasedSpineResource(spineSnapshot);

            if (ReportedVisualPostfixes.Add(key))
            {
                ModLog.Info(
                    $"已按当前选择应用 {provider.Name} 的视觉后处理：" +
                    $"{postfix.Method.DeclaringType?.FullName}.{postfix.Method.Name}。");
            }
        }
        catch (Exception exception)
        {
            if (FailedVisualPostfixes.Add(key))
            {
                ModLog.Warn(
                    $"{provider.Name} 的视觉后处理与当前游戏版本不兼容，已跳过且不会中断游戏：" +
                    exception.GetBaseException().Message);
            }
        }
    }

    private static AliasedSpineResourceSnapshot? CaptureAliasedSpineResource(
        NCreatureVisuals visuals)
    {
        var spineNode = visuals.GetNodeOrNull<Node>("%Visuals");
        if (spineNode == null)
        {
            return null;
        }

        var resource = spineNode.Get("skeleton_data_res").As<Resource>();
        if (resource == null || !TryGetCanonicalAliasPath(resource.ResourcePath, out var canonicalPath))
        {
            return null;
        }

        return new AliasedSpineResourceSnapshot(spineNode, resource, canonicalPath);
    }

    private static void RestoreAliasedSpineResource(AliasedSpineResourceSnapshot? snapshot)
    {
        if (snapshot == null || !GodotObject.IsInstanceValid(snapshot.SpineNode))
        {
            return;
        }

        var current = snapshot.SpineNode.Get("skeleton_data_res").As<Resource>();
        // A provider commonly reloads the same logical resource through its canonical path. That
        // may return a stale pre-switch cache entry, so put the already isolated selected resource
        // back. A genuinely different private path is left untouched.
        if (current == null ||
            !current.ResourcePath.Equals(snapshot.CanonicalPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        snapshot.SpineNode.Set("skeleton_data_res", snapshot.Resource);
        TryRefreshSpineAttachments(snapshot.SpineNode);
    }

    private static bool TryGetCanonicalAliasPath(string resourcePath, out string canonicalPath)
    {
        const string prefix = "res://sts2_skin_runtime/";
        canonicalPath = string.Empty;
        if (!resourcePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var relative = resourcePath[prefix.Length..];
        var sessionSeparator = relative.IndexOf('/');
        var generationSeparator = sessionSeparator < 0
            ? -1
            : relative.IndexOf('/', sessionSeparator + 1);
        if (generationSeparator < 0 || generationSeparator + 1 >= relative.Length)
        {
            return false;
        }

        canonicalPath = "res://" + relative[(generationSeparator + 1)..];
        return true;
    }

    private static void TryRefreshSpineAttachments(Node spineNode)
    {
        try
        {
            var skeleton = spineNode.Call("get_skeleton").As<GodotObject>();
            var skin = skeleton?.Call("get_skin").As<GodotObject>();
            var slots = skeleton?.Call("get_slots").As<Godot.Collections.Array>();
            var attachments = skin?.Call("get_attachments").As<Godot.Collections.Array>();
            if (slots == null || attachments == null)
            {
                return;
            }

            foreach (var entryValue in attachments)
            {
                var entry = entryValue.As<GodotObject>();
                if (entry == null)
                {
                    continue;
                }

                var slotIndex = entry.Call("get_slot_index").AsInt32();
                var attachment = entry.Call("get_attachment");
                if (attachment.VariantType == Variant.Type.Nil ||
                    slotIndex < 0 ||
                    slotIndex >= slots.Count)
                {
                    continue;
                }

                slots[slotIndex].As<GodotObject>()?.Call("set_attachment", attachment);
            }
        }
        catch
        {
            // Attachment refresh is a best-effort cache repair after a provider reloaded the same
            // skeleton path. The selected resource itself has already been restored.
        }
    }

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
    }

    private static void NotifyModDetected(Mod mod)
    {
        if (InvokeOnModDetectedMethod != null)
        {
            InvokeOnModDetectedMethod.Invoke(null, [mod]);
            return;
        }

        var handlers = (OnModDetectedField?.GetValue(null) as Delegate)?.GetInvocationList() ?? [];
        foreach (var handler in handlers)
        {
            try
            {
                handler.DynamicInvoke(mod);
            }
            catch (Exception exception)
            {
                ModLog.Warn(
                    $"通知 Mod 加载监听器 {handler.Method.DeclaringType?.FullName}.{handler.Method.Name} 失败：" +
                    exception.GetBaseException().Message);
            }
        }
    }

    private static void CleanupOldProviderNamespaces()
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         OS.GetUserDataDir(),
                         "sts2_skin_provider_namespace_*.pck"))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception)
        {
            ModLog.Warn("无法清理旧的提供者命名空间缓存：" + exception.Message);
        }
    }

    private static bool CanBypassOriginalLoader(Mod mod)
    {
        var manifest = mod.manifest;
        if (manifest?.id == null)
        {
            return false;
        }

        var mods = ModManager.Mods;
        // 即使一个公共库碰巧带有可识别的图片或场景，只要别的 Mod 声明依赖它，
        // 就必须交回游戏正常加载。否则隔离其 DLL 会让所有依赖者在反射阶段失败。
        if (IsRequiredByAnotherMod(mod, mods))
        {
            return false;
        }

        if (mods.Any(other =>
                !ReferenceEquals(other, mod) &&
                other.manifest?.id == manifest.id &&
                other.state == ModLoadState.Loaded))
        {
            return false;
        }

        if (manifest.dependencies?.Any(dependency =>
                !DependencyIsSatisfied(mods, dependency)) == true)
        {
            return false;
        }

        var circularDependencies = CircularDependenciesField!.GetValue(null) as
            IReadOnlyDictionary<string, string>;
        if (circularDependencies?.ContainsKey(manifest.id) == true)
        {
            return false;
        }

        if (manifest.minGameVersion == null)
        {
            return true;
        }

        if (!SemanticVersion.TryFromString(manifest.minGameVersion, out var minimum))
        {
            return false;
        }

        var gameVersion = GameVersionField!.GetValue(null) as SemanticVersion;
        return gameVersion == null || gameVersion.CompareTo(minimum) >= 0;
    }

    private static bool DependencyIsSatisfied(
        IEnumerable<Mod> mods,
        ModDependency dependency)
    {
        var loaded = mods.FirstOrDefault(candidate =>
            candidate.manifest?.id == dependency.id &&
            candidate.state == ModLoadState.Loaded);
        if (loaded == null)
        {
            return false;
        }

        if (dependency.minVersion == null)
        {
            return true;
        }

        return SemanticVersion.TryFromString(dependency.minVersion, out var minimum) &&
               loaded.version != null &&
               loaded.version.CompareTo(minimum) >= 0;
    }

    private static bool IsManagedProvider(Mod mod, out SkinProviderProbe provider)
    {
        try
        {
            var root = NormalizePath(mod.path);
            if (ProvidersByRoot.TryGetValue(root, out provider!))
            {
                return true;
            }

            if (!NegativeProviderRoots.Add(root))
            {
                provider = null!;
                return false;
            }

            var detected = SkinCatalog.ProbeSkinProviders([ToDescriptor(mod)])
                .FirstOrDefault(probe => probe.RootPath != null);
            if (detected == null)
            {
                provider = null!;
                return false;
            }

            ProvidersByRoot[root] = detected;
            provider = detected;
            ModLog.Info($"加载时补充识别皮肤提供者：{mod.manifest?.name ?? mod.manifest?.id}。");
            return true;
        }
        catch
        {
            provider = null!;
            return false;
        }
    }

    private static SkinModDescriptor ToDescriptor(Mod mod)
    {
        var manifest = mod.manifest!;
        return new SkinModDescriptor(
            manifest.id!,
            manifest.name ?? manifest.id!,
            manifest.hasPck
                ? Path.Combine(mod.path, manifest.id + ".pck")
                : null,
            manifest.affectsGameplay || IsRequiredByAnotherMod(mod, ModManager.Mods),
            mod.path,
            manifest.hasDll);
    }

    public static bool IsRequiredByAnotherMod(Mod mod, IEnumerable<Mod> mods)
    {
        var modId = mod.manifest?.id;
        return modId != null && mods.Any(other =>
            !ReferenceEquals(other, mod) &&
            other.manifest?.dependencies?.Any(dependency =>
                string.Equals(dependency.id, modId, StringComparison.OrdinalIgnoreCase)) == true);
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private sealed record ProviderAssembly(
        string AssemblyPath,
        string Name,
        bool HasGodotScripts,
        bool HasDeclarativeCharacterAssetReplacement);

    private sealed record ActiveProviderRuntime(
        Assembly Assembly,
        IReadOnlyList<ProviderPatch> Patches,
        IReadOnlyList<ProviderPatch> CharacterPresentationPatches,
        IReadOnlyList<ProviderPatch> NodeReadyPresentationPatches)
    {
        public Dictionary<ulong, CharacterPresentationMutation> CharacterPresentationMutations { get; } = [];
        public Dictionary<ulong, NodeReadyMutation> NodeReadyMutations { get; } = [];
        public Dictionary<ulong, NodeReadyBaseline> PendingNodeReadyBaselines { get; } = [];
    }

    private sealed record ProviderRuntimeBlueprint(
        IReadOnlyList<ProviderPatch> BehaviorPatches,
        IReadOnlyList<ProviderPatch> CharacterPresentationPatches,
        IReadOnlyList<ProviderPatch> NodeReadyPresentationPatches);

    private sealed record CharacterPresentationNodeState(
        Node Node,
        bool? Visible,
        string? Text,
        bool? ClipContents);

    private sealed record CharacterPresentationMutation(
        WeakReference<NCharacterSelectScreen> Screen,
        IReadOnlyList<WeakReference<Node>> AddedRoots,
        IReadOnlyList<CharacterPresentationVisibilityChange> VisibilityChanges,
        IReadOnlyList<CharacterPresentationTextChange> TextChanges,
        IReadOnlyList<CharacterPresentationClipChange> ClipChanges);

    private sealed record CharacterPresentationVisibilityChange(
        WeakReference<CanvasItem> Node,
        bool OriginalVisibility,
        bool AppliedVisibility);

    private sealed record CharacterPresentationTextChange(
        WeakReference<Node> Node,
        string OriginalText,
        string AppliedText);

    private sealed record CharacterPresentationClipChange(
        WeakReference<Control> Node,
        bool OriginalClipContents,
        bool AppliedClipContents);

    private sealed record NodeReadyBaseline(
        WeakReference<Node> Node,
        IReadOnlyDictionary<ulong, NodeReadyVisualState> States);

    private sealed record NodeReadyVisualState(
        WeakReference<Node> Node,
        bool? Visible,
        Color? Modulate,
        Color? SelfModulate,
        int? ZIndex,
        bool? ZAsRelative,
        Vector2? Node2DPosition,
        Vector2? Node2DScale,
        float? Node2DRotation,
        Vector2? ControlPosition,
        Vector2? ControlSize,
        Vector2? ControlScale,
        float? ControlRotation,
        Vector2? ControlPivotOffset,
        Control.MouseFilterEnum? ControlMouseFilter);

    private sealed record NodeReadyVisualChange(
        WeakReference<Node> Node,
        NodeReadyVisualState Original,
        NodeReadyVisualState Applied);

    private sealed record NodeReadyMutation(
        IReadOnlyList<WeakReference<Node>> AddedRoots,
        IReadOnlyList<NodeReadyVisualChange> Changes);

    private sealed record ProviderPatch(
        MethodBase Target,
        MethodInfo Callback,
        ProviderPatchKind Kind,
        int Priority,
        string[] Before,
        string[] After,
        bool Debug);

    private enum ProviderPatchKind
    {
        Prefix,
        Postfix,
        Transpiler,
        Finalizer
    }

    private sealed record ManagedVisualPostfix(Type TargetType, MethodInfo Method);

    private sealed record AliasedSpineResourceSnapshot(
        Node SpineNode,
        Resource Resource,
        string CanonicalPath);

}

[HarmonyPatch]
internal static class GodotScriptPathRegistrationCompatibilityPatch
{
    // Godot's ScriptManagerBridge stores script paths in a private Dictionary and uses Add rather
    // than an idempotent assignment. Complete skin providers commonly register their C# scenes
    // once from Skin Changer and once again from their own initializer; the second call otherwise
    // aborts the provider's whole visual session with a duplicate-key exception. Only an exact
    // same-path/same-Type repeat is ignored. A genuine path collision between different types is
    // deliberately left to Godot so it cannot silently select the wrong script.
    private static bool Prepare() => FindTarget() != null;

    private static MethodBase TargetMethod() =>
        FindTarget() ?? throw new MissingMethodException(
            "Godot.Bridge.ScriptManagerBridge.PathScriptTypeBiMap.Add");

    private static bool Prefix(object __instance, string scriptPath, Type scriptType)
    {
        var mapField = __instance.GetType().GetField(
            "_pathTypeMap",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (mapField?.GetValue(__instance) is not IDictionary<string, Type> map ||
            !map.TryGetValue(scriptPath, out var existingType))
        {
            return true;
        }

        return existingType != scriptType;
    }

    private static MethodBase? FindTarget()
    {
        var bridgeType = typeof(GodotObject).Assembly.GetType(
            "Godot.Bridge.ScriptManagerBridge");
        var mapType = bridgeType?.GetNestedType(
            "PathScriptTypeBiMap",
            BindingFlags.NonPublic);
        return mapType?.GetMethod(
            "Add",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(string), typeof(Type)],
            modifiers: null);
    }
}

[HarmonyPatch]
internal static class ManagedSkinModLoadPatch
{
    private static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(ModManager), "TryLoadMod");

    private static bool Prefix(Mod mod) => !ManagedSkinModLoader.TryManage(mod);
}
