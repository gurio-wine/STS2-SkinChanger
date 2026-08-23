using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Combat;
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
    private static readonly Dictionary<string, IReadOnlyList<ManagedVisualPostfix>> VisualPostfixesByProvider =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ActiveProviderRuntime> ActiveProviderRuntimes =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ReportedVisualPostfixes =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> FailedVisualPostfixes =
        new(StringComparer.OrdinalIgnoreCase);
    private static bool _initialized;
    private static bool _reflectionTargetsReady;

    public static bool IsFirstInLoadOrder { get; private set; } = true;
    public static IReadOnlyCollection<string> ProviderRoots => ProvidersByRoot.Keys;

    public static bool IsProviderAssemblyActive(Assembly assembly) =>
        ActiveProviderRuntimes.Values.Any(runtime => ReferenceEquals(runtime.Assembly, assembly));

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

        var selfIndex = Array.FindIndex(mods, mod =>
            mod.manifest?.id?.Equals(Entry.ModId, StringComparison.OrdinalIgnoreCase) == true);
        IsFirstInLoadOrder = selfIndex == 0;
        var alreadyLoaded = selfIndex <= 0
            ? []
            : mods.Take(selfIndex)
                .Where(mod => IsManagedProvider(mod, out _))
                .Select(mod => mod.manifest?.name ?? mod.manifest?.id ?? mod.path)
                .ToArray();
        if (alreadyLoaded.Length > 0)
        {
            ModLog.Warn(
                "托管加载模式仅能拦截排在本 Mod 后面的皮肤提供者。请把皮肤切换器-Skin Changer 移到 Mod 顺序最前并重启。" +
                $" 本次已提前加载：{string.Join("、", alreadyLoaded)}");
        }

        ModLog.Info(
            $"托管加载模式已识别 {ProvidersByRoot.Count} 个皮肤提供者；" +
            "其 PCK 只会按当前选择隔离读取；符合单一外观整包条件的 DLL 行为仅在选中期间启用，切走即卸载。");
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
                $"场景脚本={provider.ManagedScriptCount}；" +
                "原 PCK 未全局挂载；" +
                "只有选中该皮肤时，才会按需调用与模型 CreateVisuals 明确绑定的视觉后处理；" +
                (provider.ManagedScriptCount > 0
                    ? "只有选中该皮肤时才注册场景实例化所需的 Godot 脚本类型；"
                    : string.Empty) +
                "单一视觉组且不含独立卡牌选择的整包会在选中期间临时启用原作者行为，" +
                "其余卡牌呈现只读取 PCK 配置并由皮肤切换器自身渲染。");
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

        ProviderAssemblies[mod.manifest.id] = new ProviderAssembly(
            assemblyPath,
            mod.manifest.name ?? mod.manifest.id,
            provider.ManagedScriptCount > 0);
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
            RegisteredProviderAssemblies.Remove(provider.AssemblyPath);
            ModLog.Warn(
                $"注册 {provider.Name} 的 Godot 场景脚本失败；" +
                "仍会隔离其全局视觉补丁：" +
                exception.GetBaseException().Message);
            return false;
        }
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
    /// Activates the original initializer of each selected single-bundle skin after its complete
    /// PCK has been mounted. Resource replacement callbacks are then removed because Skin Changer
    /// owns those entry points; animation/VFX/room behavior remains active until deselection.
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

            var initializerTypes = GetLoadableTypes(assembly)
                .Select(type => (Type: type, Attribute: type.GetCustomAttribute<ModInitializerAttribute>()))
                .Where(pair => pair.Attribute != null)
                .ToArray();
            if (initializerTypes.Length == 0)
            {
                new Harmony($"{Entry.ModId}.selected.{NormalizeHarmonyId(providerId)}")
                    .PatchAll(assembly);
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

                    method.Invoke(null, null);
                }
            }

            var installedPatches = CaptureProviderPatches(assembly);
            var resourceOwnershipPatches = installedPatches
                .Where(IsManagedResourceOwnershipPatch)
                .ToArray();
            UnpatchProviderCallbacks(resourceOwnershipPatches);

            var leakedResourcePatches = CaptureProviderPatches(assembly)
                .Where(IsManagedResourceOwnershipPatch)
                .ToArray();
            if (leakedResourcePatches.Length > 0)
            {
                throw new InvalidOperationException(
                    $"仍有 {leakedResourcePatches.Length} 个资源替换补丁未能隔离");
            }

            var behaviorPatches = CaptureProviderPatches(assembly);
            ActiveProviderRuntimes[providerId] = new ActiveProviderRuntime(assembly, behaviorPatches);
            ModLog.Info(
                $"已按当前选择启用 {provider.Name} 的完整视觉会话：" +
                $"资源整包已隔离挂载，{resourceOwnershipPatches.Length} 个重复资源入口已交由本 Mod 接管，" +
                $"保留 {behaviorPatches.Count} 个原作者动画/场景行为补丁；切换离开后会自动卸载。");
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

    private static void DeactivateProvider(string providerId)
    {
        if (!ActiveProviderRuntimes.Remove(providerId, out var runtime))
        {
            return;
        }

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
                yield return new ProviderPatch(target, entry.Patch.PatchMethod, entry.Kind);
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

        if (patch.Target.Name.Equals(nameof(CharacterModel.CreateVisuals), StringComparison.Ordinal))
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

    /// <summary>
    /// Replays presentation-only postfixes after Skin Changer has rebuilt the current character
    /// selection baseline. This makes behavior-driven skins reversible in both directions without
    /// calling SelectCharacter again (which would replay SFX, screen shake and lobby mutations).
    /// </summary>
    public static void ReplaySelectedCharacterPresentation(
        string providerId,
        NCharacterSelectScreen screen,
        NCharacterSelectButton button,
        CharacterModel character)
    {
        if (!ActiveProviderRuntimes.TryGetValue(providerId, out var runtime))
        {
            return;
        }

        var replayed = 0;
        foreach (var patch in runtime.Patches.Where(patch =>
                     patch.Kind == ProviderPatchKind.Postfix &&
                     IsCharacterPresentationTarget(patch.Target)))
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

        if (replayed > 0)
        {
            ModLog.Info($"已在恢复游戏基线后重放 {providerId} 的 {replayed} 个选角呈现步骤。");
        }
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
            return assembly;
        }

        var loadContext = AssemblyLoadContext.GetLoadContext(Assembly.GetExecutingAssembly());
        return loadContext?.LoadFromAssemblyPath(provider.AssemblyPath) ??
               Assembly.LoadFrom(provider.AssemblyPath);
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
        bool HasGodotScripts);

    private sealed record ActiveProviderRuntime(
        Assembly Assembly,
        IReadOnlyList<ProviderPatch> Patches);

    private sealed record ProviderPatch(
        MethodBase Target,
        MethodInfo Callback,
        ProviderPatchKind Kind);

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
internal static class ManagedSkinModLoadPatch
{
    private static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(ModManager), "TryLoadMod");

    private static bool Prefix(Mod mod) => !ManagedSkinModLoader.TryManage(mod);
}
