using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Combat;
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
    private static readonly HashSet<string> ReportedVisualPostfixes =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> FailedVisualPostfixes =
        new(StringComparer.OrdinalIgnoreCase);
    private static bool _initialized;
    private static bool _reflectionTargetsReady;

    public static bool IsFirstInLoadOrder { get; private set; } = true;
    public static IReadOnlyCollection<string> ProviderRoots => ProvidersByRoot.Keys;

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
            "其 PCK 只会按当前选择隔离读取，DLL 初始化器和全局补丁不会执行。");
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
                "原 PCK 未全局挂载，DLL 初始化器和全局补丁均不执行；" +
                "只有选中该皮肤时，才会按需调用与模型 CreateVisuals 明确绑定的视觉后处理；" +
                (provider.ManagedScriptCount > 0
                    ? "只有选中该皮肤时才注册场景实例化所需的 Godot 脚本类型；"
                    : string.Empty) +
                "卡牌呈现只读取 PCK 配置并由皮肤切换器自身渲染。");
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
                "未执行其初始化器或注册全局 Harmony 补丁。");
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
    /// Runs only the selected provider's Harmony postfixes for model visual creation. The provider
    /// assembly is never patched into Harmony globally: this preserves isolation while retaining
    /// skin-specific transforms, removed attachment nodes and other scene finishing work that cannot
    /// be represented by replacement textures alone.
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
