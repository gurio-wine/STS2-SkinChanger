using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using System.Reflection;
using System.Text.RegularExpressions;

namespace STS2SkinChanger.Core;

internal static partial class VisualPatchGuard
{
    private static readonly Harmony Harmony = new(Entry.ModId);
    private static readonly object CardPatchSync = new();
    private static readonly List<RemovedCardPatch> RemovedCardPatches = [];
    private static readonly List<ScopedCardModelPatch> ScopedCardModelPatches = [];
    private static readonly Dictionary<string, CardPresentationProvider> CardPresentationProviders =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ReplayWarnings = new(StringComparer.Ordinal);
    [ThreadStatic]
    private static Stack<CardPresentationScope>? _cardPresentationScopes;
    private static readonly string[] VisualNameTokens =
    [
        "Visual", "Scene", "Portrait", "Icon", "Texture", "Sprite", "Atlas",
        "Background", "MapMarker", "Animation", "Sfx", "Sound", "Audio", "Voice"
    ];
    private static readonly string[] VisualTypeNameTokens =
    [
        "Visual", "Vfx", "Spine", "Sprite", "Portrait", "Icon", "Character",
        "Creature", "Merchant", "Card", "Animation", "Audio", "Sound"
    ];

    public static int DiscoverCardPresentationPatches(string providerRoot, Assembly assembly)
    {
        var registered = 0;
        var registeredMethods = new HashSet<MethodInfo>();
        foreach (var type in GetLoadableTypes(assembly))
        {
            var postfixes = type.GetMethods(
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => method.Name == "Postfix" ||
                                 method.Name.EndsWith("Postfix", StringComparison.Ordinal) ||
                                 method.GetCustomAttribute<HarmonyPostfix>() != null)
                .ToArray();
            if (postfixes.Length == 0)
            {
                continue;
            }

            MethodBase[] dynamicTargets;
            try
            {
                dynamicTargets = ResolveDynamicTargets(type);
            }
            catch
            {
                dynamicTargets = [];
            }

            var classAnnotations = type.GetCustomAttributes<HarmonyPatch>(inherit: true)
                .Select(annotation => annotation.info)
                .ToArray();
            foreach (var postfix in postfixes)
            {
                MethodBase[] targets;
                try
                {
                    var annotations = classAnnotations
                        .Concat(postfix.GetCustomAttributes<HarmonyPatch>(inherit: true)
                            .Select(annotation => annotation.info))
                        .ToArray();
                    targets = dynamicTargets.Length > 0
                        ? dynamicTargets
                        : ResolveAnnotatedTargets(annotations);
                    if (targets.Length == 0 &&
                        postfix.Name.Contains("NCard", StringComparison.OrdinalIgnoreCase))
                    {
                        targets = [AccessTools.Method(typeof(NCard), "Reload")];
                    }
                }
                catch
                {
                    continue;
                }

                foreach (var target in targets.Where(target => target.DeclaringType != null))
                {
                    if (typeof(NCard).IsAssignableFrom(target.DeclaringType))
                    {
                        if (IsOwnedPortraitPatch(postfix))
                        {
                            continue;
                        }
                        if (RememberCardPatch(providerRoot, target, postfix))
                        {
                            registered++;
                            registeredMethods.Add(postfix);
                        }
                    }
                    else if (IsScopedCardRarityPatch(target) &&
                             RememberScopedCardModelPatch(providerRoot, target, postfix))
                    {
                        registered++;
                        registeredMethods.Add(postfix);
                    }
                }
            }
        }

        if (registered > 0)
        {
            RememberCardPresentationProvider(providerRoot, assembly, registeredMethods);
            ModLog.Info($"已登记 {registered} 个隔离卡牌呈现补丁：{assembly.GetName().Name}");
        }

        return registered;
    }

    public static IReadOnlyCollection<string> GetCardPresentationResourcePaths(string providerRoot)
    {
        var root = NormalizeRoot(providerRoot);
        if (root == null)
        {
            return [];
        }

        lock (CardPatchSync)
        {
            return CardPresentationProviders.TryGetValue(root, out var provider)
                ? provider.ResourcePaths.ToArray()
                : [];
        }
    }

    public static bool InitializeCardPresentationProvider(string providerRoot)
    {
        var root = NormalizeRoot(providerRoot);
        if (root == null)
        {
            return false;
        }

        CardPresentationProvider provider;
        lock (CardPatchSync)
        {
            if (!CardPresentationProviders.TryGetValue(root, out provider!))
            {
                return true;
            }
            if (provider.Initialized)
            {
                return true;
            }
        }

        foreach (var initializer in provider.RegistryInitializers)
        {
            try
            {
                initializer.Invoke(null, null);
            }
            catch (Exception exception)
            {
                ModLog.Warn(
                    $"无法初始化隔离卡牌呈现注册表 {initializer.DeclaringType?.FullName}." +
                    $"{initializer.Name}：{exception.GetBaseException().Message}");
                return false;
            }
        }

        lock (CardPatchSync)
        {
            provider.Initialized = true;
        }
        if (provider.RegistryInitializers.Length > 0)
        {
            ModLog.Info(
                $"已初始化 {provider.RegistryInitializers.Length} 个隔离卡牌呈现注册表：" +
                provider.AssemblyName);
        }
        return true;
    }

    private static bool IsOwnedPortraitPatch(MethodInfo patchMethod)
    {
        var name = $"{patchMethod.DeclaringType?.Name}.{patchMethod.Name}";
        if (new[]
            {
                "Frame", "Border", "Material", "Layout", "TextPatch", "TextReplacement",
                "Title", "Description", "Banner", "Style"
            }
            .Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return new[] { "Portrait", "CardArt", "Artwork", "TextureFilter" }
            .Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsScopedCardRarityPatch(MethodBase target) =>
        target.DeclaringType != null &&
        typeof(CardModel).IsAssignableFrom(target.DeclaringType) &&
        target.Name == "get_Rarity";

    private static void RememberCardPresentationProvider(
        string providerRoot,
        Assembly assembly,
        IReadOnlyCollection<MethodInfo> patchMethods)
    {
        var root = NormalizeRoot(providerRoot);
        if (root == null)
        {
            return;
        }

        var reachableMethods = DiscoverReachableProviderMethods(assembly, patchMethods);
        var initializers = reachableMethods
            .OfType<MethodInfo>()
            .Where(method => method.Name == "EnsureLoaded" &&
                             method.ReturnType == typeof(void) &&
                             method.GetParameters().Length == 0)
            .Distinct()
            .ToArray();
        var resourcePaths = DiscoverMethodResourcePaths(reachableMethods)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        lock (CardPatchSync)
        {
            CardPresentationProviders[root] = new CardPresentationProvider(
                assembly.GetName().Name ?? assembly.FullName ?? "unknown",
                initializers,
                resourcePaths);
        }
        if (initializers.Length > 0 || resourcePaths.Length > 0)
        {
            ModLog.Info(
                $"已发现卡牌呈现依赖：{assembly.GetName().Name}，" +
                $"注册表={initializers.Length}，资源入口={resourcePaths.Length}");
        }
    }

    private static IReadOnlyCollection<MethodBase> DiscoverReachableProviderMethods(
        Assembly assembly,
        IEnumerable<MethodInfo> roots)
    {
        var visited = new HashSet<MethodBase>();
        var queue = new Queue<MethodBase>(roots);
        while (queue.TryDequeue(out var method))
        {
            if (!visited.Add(method))
            {
                continue;
            }

            if (method.DeclaringType?.TypeInitializer is { } typeInitializer &&
                typeInitializer.Module.Assembly == assembly &&
                !visited.Contains(typeInitializer))
            {
                queue.Enqueue(typeInitializer);
            }

            foreach (var calledMethod in DiscoverCalledMethods(method))
            {
                if (calledMethod.Module.Assembly == assembly && !visited.Contains(calledMethod))
                {
                    queue.Enqueue(calledMethod);
                }
            }
        }

        return visited;
    }

    private static IEnumerable<MethodBase> DiscoverCalledMethods(MethodBase method)
    {
        byte[]? il;
        try
        {
            il = method.GetMethodBody()?.GetILAsByteArray();
        }
        catch
        {
            yield break;
        }
        if (il == null)
        {
            yield break;
        }

        var typeArguments = method.DeclaringType?.IsGenericType == true
            ? method.DeclaringType.GetGenericArguments()
            : null;
        var methodArguments = method is MethodInfo { IsGenericMethod: true } methodInfo
            ? methodInfo.GetGenericArguments()
            : null;
        for (var offset = 0; offset + 4 < il.Length; offset++)
        {
            if (il[offset] is not (0x28 or 0x6F or 0x73))
            {
                continue;
            }

            MethodBase? calledMethod;
            try
            {
                calledMethod = method.Module.ResolveMethod(
                    BitConverter.ToInt32(il, offset + 1),
                    typeArguments,
                    methodArguments);
            }
            catch
            {
                continue;
            }
            if (calledMethod != null)
            {
                yield return calledMethod;
            }
        }
    }

    private static IEnumerable<string> DiscoverMethodResourcePaths(IEnumerable<MethodBase> methods)
    {
        foreach (var method in methods)
        {
            byte[]? il;
            try
            {
                il = method.GetMethodBody()?.GetILAsByteArray();
            }
            catch
            {
                continue;
            }
            if (il == null)
            {
                continue;
            }

            for (var offset = 0; offset + 4 < il.Length; offset++)
            {
                // ldstr <metadata-token>. ResolveString validates the token, so accidental 0x72
                // bytes inside another operand are harmless.
                if (il[offset] != 0x72)
                {
                    continue;
                }

                string value;
                try
                {
                    value = method.Module.ResolveString(BitConverter.ToInt32(il, offset + 1));
                }
                catch
                {
                    continue;
                }

                foreach (Match match in ResourceLiteralRegex().Matches(value))
                {
                    yield return match.Value;
                }
            }
        }
    }

    private static MethodBase[] ResolveDynamicTargets(Type patchType)
    {
        var targetMethods = patchType.GetMethod(
            "TargetMethods",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);
        if (targetMethods?.Invoke(null, null) is IEnumerable<MethodBase> many)
        {
            return many.Where(method => method != null).ToArray();
        }

        var targetMethod = patchType.GetMethod(
            "TargetMethod",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);
        return targetMethod?.Invoke(null, null) is MethodBase one ? [one] : [];
    }

    private static MethodBase[] ResolveAnnotatedTargets(IReadOnlyCollection<HarmonyMethod> annotations)
    {
        if (annotations.Count == 0)
        {
            return [];
        }

        var target = HarmonyMethod.Merge(annotations.ToList());
        if (target.method != null)
        {
            return [target.method];
        }

        if (target.declaringType == null)
        {
            return [];
        }

        MethodBase? resolved = target.methodType switch
        {
            MethodType.Getter => AccessTools.PropertyGetter(target.declaringType, target.methodName),
            MethodType.Setter => AccessTools.PropertySetter(target.declaringType, target.methodName),
            MethodType.Constructor => AccessTools.Constructor(target.declaringType, target.argumentTypes),
            MethodType.StaticConstructor => target.declaringType.TypeInitializer,
            _ when target.methodName != null =>
                AccessTools.Method(target.declaringType, target.methodName, target.argumentTypes),
            _ => null
        };
        return resolved == null ? [] : [resolved];
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type != null).Cast<Type>();
        }
    }

    /*
     * Only the helper methods above execute during provider discovery. Provider initializers and
     * Harmony patch installation remain disabled; registered postfixes run later for the selected
     * card through ReplaySelectedCardPostfixes.
     */

    public static int RemoveProviderVisualPatches(IEnumerable<string> providerRoots)
    {
        var roots = providerRoots
            .Select(NormalizeRoot)
            .Where(root => root != null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (roots.Length == 0)
        {
            return 0;
        }

        var removed = 0;
        var capturedCardPatches = 0;
        var affectedTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in HarmonyLib.Harmony.GetAllPatchedMethods()
                     .Where(IsVisualTarget)
                     .ToArray())
        {
            var patchInfo = HarmonyLib.Harmony.GetPatchInfo(target);
            if (patchInfo == null)
            {
                continue;
            }

            var providerPatches = EnumeratePatches(patchInfo)
                .Select(entry => (entry.Patch, entry.Kind, Root: GetProviderRoot(entry.Patch, roots)))
                .Where(entry => entry.Root != null)
                .DistinctBy(entry => (entry.Patch.PatchMethod, entry.Kind))
                .ToArray();
            foreach (var entry in providerPatches)
            {
                try
                {
                    if (typeof(NCard).IsAssignableFrom(target.DeclaringType) &&
                        entry.Kind == ProviderPatchKind.Postfix)
                    {
                        if (RememberCardPatch(entry.Root!, target, entry.Patch.PatchMethod))
                        {
                            capturedCardPatches++;
                        }
                    }

                    Harmony.Unpatch(target, entry.Patch.PatchMethod);
                    removed++;
                    affectedTargets.Add($"{target.DeclaringType?.Name}.{target.Name}");
                }
                catch (Exception exception)
                {
                    ModLog.Warn(
                        $"无法移除视觉补丁 {entry.Patch.owner}/" +
                        $"{entry.Patch.PatchMethod.DeclaringType?.FullName}.{entry.Patch.PatchMethod.Name}：" +
                        exception.Message);
                }
            }
        }

        if (removed > 0)
        {
            ModLog.Info(
                $"已移除 {removed} 个提供者皮肤呈现补丁，保留其余 DLL 功能；目标：" +
                string.Join("、", affectedTargets.OrderBy(name => name)) +
                $"；登记卡牌呈现重放={capturedCardPatches}，" +
                $"不参与重放={removed - capturedCardPatches}");
        }

        return removed;
    }

    public static int ReplaySelectedCardPostfixes(
        NCard card,
        MethodBase originalMethod,
        object[] originalArguments,
        string? providerRoot)
    {
        var root = providerRoot == null ? null : NormalizeRoot(providerRoot);
        if (root == null)
        {
            return 0;
        }

        RemovedCardPatch[] patches;
        lock (CardPatchSync)
        {
            patches = RemovedCardPatches
                .Where(patch => patch.ProviderRoot.Equals(root, StringComparison.OrdinalIgnoreCase) &&
                                IsCardPresentationStage(patch.Target, originalMethod))
                .ToArray();
        }

        var applied = 0;
        foreach (var patch in patches)
        {
            try
            {
                if (!TryBuildPatchArguments(
                        patch.PatchMethod,
                        card,
                        patch.Target,
                        SameMethod(patch.Target, originalMethod) ? originalArguments : [],
                        out var invokeArguments))
                {
                    WarnReplayOnce(
                        patch,
                        "参数包含无法在隔离呈现阶段还原的 Harmony 状态");
                    continue;
                }

                patch.PatchMethod.Invoke(null, invokeArguments);
                applied++;
            }
            catch (Exception exception)
            {
                WarnReplayOnce(patch, exception.GetBaseException().Message);
            }
        }

        return applied;
    }

    public static void ReplaySelectedCardRarityPostfixes(
        CardModel card,
        MethodBase originalMethod,
        ref CardRarity result)
    {
        if (_cardPresentationScopes == null ||
            !_cardPresentationScopes.TryPeek(out var activeScope) ||
            !ReferenceEquals(activeScope.Card.Model, card))
        {
            return;
        }

        var providerRoot = activeScope.ProviderRoot;
        if (providerRoot == null || !SkinService.PrepareCardPresentationProvider(providerRoot))
        {
            return;
        }

        var root = NormalizeRoot(providerRoot);
        if (root == null)
        {
            return;
        }

        ScopedCardModelPatch[] patches;
        lock (CardPatchSync)
        {
            patches = ScopedCardModelPatches
                .Where(patch => patch.ProviderRoot.Equals(root, StringComparison.OrdinalIgnoreCase) &&
                                SameMethod(patch.Target, originalMethod))
                .ToArray();
        }

        foreach (var patch in patches)
        {
            try
            {
                var parameters = patch.PatchMethod.GetParameters();
                var arguments = new object?[parameters.Length];
                var resultIndex = -1;
                var supported = true;
                for (var index = 0; index < parameters.Length; index++)
                {
                    var parameter = parameters[index];
                    var parameterType = parameter.ParameterType.IsByRef
                        ? parameter.ParameterType.GetElementType()!
                        : parameter.ParameterType;
                    if (parameter.Name == "__instance" || typeof(CardModel).IsAssignableFrom(parameterType))
                    {
                        arguments[index] = card;
                    }
                    else if (parameter.Name == "__result" && parameterType == typeof(CardRarity))
                    {
                        arguments[index] = result;
                        resultIndex = index;
                    }
                    else if (parameter.Name == "__originalMethod")
                    {
                        arguments[index] = originalMethod;
                    }
                    else
                    {
                        supported = false;
                        break;
                    }
                }

                if (!supported)
                {
                    continue;
                }

                patch.PatchMethod.Invoke(null, arguments);
                if (resultIndex >= 0 && arguments[resultIndex] is CardRarity updatedResult)
                {
                    result = updatedResult;
                }
            }
            catch (Exception exception)
            {
                WarnReplayOnce(
                    new RemovedCardPatch(patch.ProviderRoot, patch.Target, patch.PatchMethod),
                    exception.GetBaseException().Message);
            }
        }
    }

    public static void EnterCardPresentationScope(NCard card)
    {
        _cardPresentationScopes ??= new Stack<CardPresentationScope>();
        var providerRoot = _cardPresentationScopes.TryPeek(out var parent) &&
                           ReferenceEquals(parent.Card.Model, card.Model)
            ? parent.ProviderRoot
            : card.Model == null
                ? null
                : SkinService.GetCardPresentationProviderRoot(card.Model);
        _cardPresentationScopes.Push(new CardPresentationScope(card, providerRoot));
    }

    public static bool TryGetActiveCardPresentationProviderRoot(
        NCard card,
        out string? providerRoot)
    {
        if (_cardPresentationScopes != null &&
            _cardPresentationScopes.TryPeek(out var activeScope) &&
            ReferenceEquals(activeScope.Card, card))
        {
            providerRoot = activeScope.ProviderRoot;
            return true;
        }

        providerRoot = null;
        return false;
    }

    public static void ExitCardPresentationScope(NCard card)
    {
        if (_cardPresentationScopes == null || _cardPresentationScopes.Count == 0)
        {
            return;
        }

        if (ReferenceEquals(_cardPresentationScopes.Peek().Card, card))
        {
            _cardPresentationScopes.Pop();
            return;
        }

        // 异常或第三方补丁打乱嵌套顺序时，只移除对应实例，避免后续非 UI 的
        // CardModel.Rarity 读取被误判为仍处在卡牌渲染阶段。
        var remaining = _cardPresentationScopes
            .Where(candidate => !ReferenceEquals(candidate.Card, card))
            .Reverse()
            .ToArray();
        _cardPresentationScopes.Clear();
        foreach (var candidate in remaining)
        {
            _cardPresentationScopes.Push(candidate);
        }
    }

    private static IEnumerable<(HarmonyLib.Patch Patch, ProviderPatchKind Kind)> EnumeratePatches(
        HarmonyLib.Patches patches)
    {
        foreach (var patch in patches.Prefixes)
        {
            yield return (patch, ProviderPatchKind.Prefix);
        }
        foreach (var patch in patches.Postfixes)
        {
            yield return (patch, ProviderPatchKind.Postfix);
        }
        foreach (var patch in patches.Transpilers)
        {
            yield return (patch, ProviderPatchKind.Transpiler);
        }
        foreach (var patch in patches.Finalizers)
        {
            yield return (patch, ProviderPatchKind.Finalizer);
        }
    }

    private static bool RememberCardPatch(string providerRoot, MethodBase target, MethodInfo patchMethod)
    {
        lock (CardPatchSync)
        {
            if (RemovedCardPatches.Any(patch =>
                    patch.ProviderRoot.Equals(providerRoot, StringComparison.OrdinalIgnoreCase) &&
                    SameMethod(patch.Target, target) &&
                    patch.PatchMethod == patchMethod))
            {
                return false;
            }

            RemovedCardPatches.Add(new RemovedCardPatch(providerRoot, target, patchMethod));
            return true;
        }
    }

    private static bool RememberScopedCardModelPatch(
        string providerRoot,
        MethodBase target,
        MethodInfo patchMethod)
    {
        var root = NormalizeRoot(providerRoot);
        if (root == null)
        {
            return false;
        }

        lock (CardPatchSync)
        {
            if (ScopedCardModelPatches.Any(patch =>
                    patch.ProviderRoot.Equals(root, StringComparison.OrdinalIgnoreCase) &&
                    SameMethod(patch.Target, target) &&
                    patch.PatchMethod == patchMethod))
            {
                return false;
            }

            ScopedCardModelPatches.Add(new ScopedCardModelPatch(root, target, patchMethod));
            return true;
        }
    }

    private static bool TryBuildPatchArguments(
        MethodInfo patchMethod,
        NCard card,
        MethodBase originalMethod,
        object[] originalArguments,
        out object?[] invokeArguments)
    {
        var targetParameters = originalMethod.GetParameters();
        invokeArguments = new object?[patchMethod.GetParameters().Length];
        for (var index = 0; index < invokeArguments.Length; index++)
        {
            var parameter = patchMethod.GetParameters()[index];
            var name = parameter.Name ?? string.Empty;
            var parameterType = parameter.ParameterType.IsByRef
                ? parameter.ParameterType.GetElementType()!
                : parameter.ParameterType;
            if (name == "__instance" ||
                typeof(NCard).IsAssignableFrom(parameterType) ||
                parameterType == typeof(object) &&
                patchMethod.Name.Contains("NCard", StringComparison.OrdinalIgnoreCase))
            {
                invokeArguments[index] = card;
                continue;
            }
            if (name == "__originalMethod")
            {
                invokeArguments[index] = originalMethod;
                continue;
            }
            if (name == "__args")
            {
                invokeArguments[index] = originalArguments;
                continue;
            }
            if (name.StartsWith("___", StringComparison.Ordinal))
            {
                var field = AccessTools.Field(originalMethod.DeclaringType, name[3..]);
                if (field == null)
                {
                    return false;
                }
                invokeArguments[index] = field.GetValue(card);
                continue;
            }
            if (name.StartsWith("__", StringComparison.Ordinal))
            {
                return false;
            }

            var targetIndex = Array.FindIndex(targetParameters, target => target.Name == name);
            if (targetIndex >= 0 && targetIndex < originalArguments.Length)
            {
                invokeArguments[index] = originalArguments[targetIndex];
                continue;
            }
            if (parameter.HasDefaultValue)
            {
                invokeArguments[index] = parameter.DefaultValue is DBNull ? null : parameter.DefaultValue;
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool SameMethod(MethodBase left, MethodBase right)
    {
        try
        {
            return left.Module == right.Module && left.MetadataToken == right.MetadataToken;
        }
        catch
        {
            return left == right;
        }
    }

    private static bool IsCardPresentationStage(MethodBase patchTarget, MethodBase currentTarget) =>
        SameMethod(patchTarget, currentTarget) ||
        (currentTarget.Name == nameof(NCard.UpdateVisuals) && patchTarget.Name == "Reload" &&
         typeof(NCard).IsAssignableFrom(patchTarget.DeclaringType));

    private static void WarnReplayOnce(RemovedCardPatch patch, string reason)
    {
        var key = patch.PatchMethod.Module.ModuleVersionId + ":" + patch.PatchMethod.MetadataToken;
        lock (CardPatchSync)
        {
            if (!ReplayWarnings.Add(key))
            {
                return;
            }
        }

        ModLog.Warn(
            $"无法隔离重放卡牌呈现补丁 {patch.PatchMethod.DeclaringType?.FullName}." +
            $"{patch.PatchMethod.Name}：{reason}");
    }

    private static bool IsVisualTarget(MethodBase target)
    {
        var declaringType = target.DeclaringType;
        if (declaringType == null)
        {
            return false;
        }

        if (declaringType == typeof(AssetCache))
        {
            return target.Name is "GetAsset" or "GetScene" or "GetTexture2D";
        }

        if (declaringType == typeof(AtlasManager))
        {
            return target.Name is "GetSprite" or "LoadAtlas" or "LoadAtlasInternal";
        }

        if (declaringType == typeof(SceneHelper) || declaringType == typeof(ImageHelper))
        {
            return target.Name.Contains("Path", StringComparison.OrdinalIgnoreCase) ||
                   target.Name.Contains("Load", StringComparison.OrdinalIgnoreCase) ||
                   target.Name.Contains("Instantiate", StringComparison.OrdinalIgnoreCase);
        }

        if (typeof(CharacterModel).IsAssignableFrom(declaringType) ||
            typeof(MonsterModel).IsAssignableFrom(declaringType) ||
            typeof(EventModel).IsAssignableFrom(declaringType) ||
            typeof(CardModel).IsAssignableFrom(declaringType))
        {
            return HasVisualName(target.Name) || ReturnsVisualValue(target);
        }

        if (typeof(NCreatureVisuals).IsAssignableFrom(declaringType))
        {
            return true;
        }

        if (typeof(NCard).IsAssignableFrom(declaringType))
        {
            return target.Name is "Reload" or "UpdateVisuals" or "UpdatePortrait" ||
                   HasVisualName(target.Name);
        }

        var namespaceName = declaringType.Namespace ?? string.Empty;
        // MegaSpine 绑定全部是骨骼/动画呈现 API，属于视觉接管范围。
        if (namespaceName.StartsWith("MegaCrit.Sts2.Core.Bindings.MegaSpine", StringComparison.Ordinal))
        {
            return true;
        }

        if (!namespaceName.StartsWith("MegaCrit.Sts2.Core.Nodes", StringComparison.Ordinal))
        {
            return false;
        }

        if (namespaceName.Contains(".Vfx", StringComparison.OrdinalIgnoreCase) ||
            VisualTypeNameTokens.Any(token =>
                declaringType.Name.Contains(token, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return HasVisualName(target.Name) &&
               (ReturnsVisualValue(target) ||
                target.GetParameters().Any(parameter => IsVisualValueType(parameter.ParameterType)));
    }

    private static bool HasVisualName(string name) =>
        VisualNameTokens.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static bool ReturnsVisualValue(MethodBase target) =>
        target is MethodInfo method && IsVisualValueType(method.ReturnType);

    private static bool IsVisualValueType(Type type)
    {
        if (type.IsByRef)
        {
            type = type.GetElementType()!;
        }

        return typeof(Texture2D).IsAssignableFrom(type) ||
               typeof(PackedScene).IsAssignableFrom(type) ||
               typeof(NCreatureVisuals).IsAssignableFrom(type);
    }

    private static string? GetProviderRoot(HarmonyLib.Patch patch, IReadOnlyList<string> roots)
    {
        var location = patch.PatchMethod.Module.Assembly.Location;
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        var assemblyPath = NormalizeRoot(location);
        if (assemblyPath == null)
        {
            return null;
        }

        // 嵌套 Mod 目录下取最长匹配的根，避免把子目录 Mod 的补丁归属到父目录 Mod。
        return roots
            .Where(root => assemblyPath.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(root => root.Length)
            .FirstOrDefault();
    }

    private static string? NormalizeRoot(string path)
    {
        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return null;
        }
    }

    private enum ProviderPatchKind
    {
        Prefix,
        Postfix,
        Transpiler,
        Finalizer
    }

    private sealed record RemovedCardPatch(
        string ProviderRoot,
        MethodBase Target,
        MethodInfo PatchMethod);

    private sealed record ScopedCardModelPatch(
        string ProviderRoot,
        MethodBase Target,
        MethodInfo PatchMethod);

    private sealed record CardPresentationScope(NCard Card, string? ProviderRoot);

    private sealed class CardPresentationProvider(
        string assemblyName,
        MethodInfo[] registryInitializers,
        string[] resourcePaths)
    {
        public string AssemblyName { get; } = assemblyName;
        public MethodInfo[] RegistryInitializers { get; } = registryInitializers;
        public string[] ResourcePaths { get; } = resourcePaths;
        public bool Initialized { get; set; }
    }

    [GeneratedRegex("res://[^\\s\\\"'<>|]*", RegexOptions.IgnoreCase)]
    private static partial Regex ResourceLiteralRegex();
}
