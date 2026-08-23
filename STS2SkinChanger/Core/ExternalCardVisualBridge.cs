using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using System.Reflection;

namespace STS2SkinChanger.Core;

/// <summary>
/// Lets live card editors keep ownership of layers that the player explicitly edited.
/// Discovery is capability based so the bridge is not tied to a workshop id or install path.
/// </summary>
internal static class ExternalCardVisualBridge
{
    private static readonly object Sync = new();
    private static readonly List<ICardVisualOwnershipAdapter> Adapters = [];
    private static readonly HashSet<Assembly> ScannedAssemblies = [];
    private static readonly HashSet<string> LoggedFailures = new(StringComparer.Ordinal);
    private static volatile bool _discoveryDirty = true;

    static ExternalCardVisualBridge()
    {
        AppDomain.CurrentDomain.AssemblyLoad += (_, _) => _discoveryDirty = true;
    }

    public static ExternalCardVisualOwnership GetOwnership(CardModel card)
    {
        var ownership = default(ExternalCardVisualOwnership);
        foreach (var adapter in EnumerateAdapters())
        {
            ownership = ownership.Merge(adapter.GetOwnership(card));
        }
        return ownership;
    }

    private static ICardVisualOwnershipAdapter[] EnumerateAdapters()
    {
        lock (Sync)
        {
            if (_discoveryDirty)
            {
                DiscoverAdapters();
            }

            return Adapters.ToArray();
        }
    }

    private static void DiscoverAdapters()
    {
        _discoveryDirty = false;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!ScannedAssemblies.Add(assembly) || IsFrameworkAssembly(assembly))
            {
                continue;
            }

            try
            {
                var types = GetLoadableTypes(assembly);
                var identityType = types.FirstOrDefault(type =>
                    type.Name.Equals("CardVisualIdentity", StringComparison.Ordinal));
                var portraitResolverType = types.FirstOrDefault(type =>
                    type.Name.Equals("CardPortraitReplacementResolver", StringComparison.Ordinal));
                if (identityType != null && portraitResolverType != null)
                {
                    var resolveIdentity = identityType.GetMethods(AllStatic)
                        .FirstOrDefault(method =>
                            method.Name.Equals("Resolve", StringComparison.Ordinal) &&
                            method.ReturnType == typeof(string) &&
                            method.GetParameters() is [{ ParameterType: var parameterType }] &&
                            AcceptsCardModel(parameterType));
                    var hasPortrait = portraitResolverType.GetMethods(AllStatic)
                        .FirstOrDefault(method =>
                            method.Name.Equals("HasAnyPortraitReplacement", StringComparison.Ordinal) &&
                            method.ReturnType == typeof(bool) &&
                            method.GetParameters() is
                            [
                                { ParameterType: var cardType },
                                { ParameterType: var idType }
                            ] &&
                            AcceptsCardModel(cardType) &&
                            idType == typeof(string));
                    if (resolveIdentity != null && hasPortrait != null)
                    {
                        var frameProbe = CreateRegistryProbe(
                            types,
                            "CardFrameOverrideRegistry",
                            "TryGetOverride");
                        var textProbe = CreateRegistryProbe(
                            types,
                            "CardTextOverrideRegistry",
                            "TryGetOverride");
                        Adapters.Add(new CardEditorAdapter(
                            assembly,
                            resolveIdentity,
                            hasPortrait,
                            frameProbe,
                            textProbe));
                        ModLog.Info(
                            $"检测到外部卡牌视觉管理器 {assembly.GetName().Name}；" +
                            "它明确编辑的卡图、卡框和文字层将优先于皮肤切换器。");
                    }
                }

                var fullPresentationAdapter = CreateFullCardPresentationAdapter(assembly, types);
                if (fullPresentationAdapter != null)
                {
                    Adapters.Add(fullPresentationAdapter);
                    ModLog.Info(
                        $"检测到外部完整卡牌呈现系统 {assembly.GetName().Name}；" +
                        "启用中的完整卡图、卡框和文字布局将保留其最终显示权。");
                }
            }
            catch (Exception exception)
            {
                LogFailure(assembly, "能力探测", exception);
            }
        }
    }

    private static FullCardPresentationAdapter? CreateFullCardPresentationAdapter(
        Assembly assembly,
        IReadOnlyCollection<Type> types)
    {
        MethodInfo? hasPresentation = null;
        MethodInfo? isEnabled = null;
        MethodInfo? getCurrent = null;
        foreach (var type in types)
        {
            hasPresentation ??= FindCardStateMethod(type, "HasSignature", typeof(bool));
            isEnabled ??= FindCardStateMethod(type, "IsEnableSignature", typeof(bool));
            getCurrent ??= FindCardStateMethod(type, "GetCurrentSignature", returnType: null);
        }

        var hasReloadLifecycle = types.Any(type =>
            FindNodeMethod(type, "AfterReload", typeof(bool)) != null &&
            FindNodeMethod(type, "ShowSignature", typeof(void)) != null &&
            FindNodeMethod(type, "HideSignature", typeof(void)) != null);
        return hasPresentation != null && isEnabled != null && getCurrent != null && hasReloadLifecycle
            ? new FullCardPresentationAdapter(assembly, hasPresentation, isEnabled, getCurrent)
            : null;
    }

    private static MethodInfo? FindCardStateMethod(
        Type type,
        string name,
        Type? returnType) =>
        type.GetMethods(AllStatic).FirstOrDefault(method =>
            method.Name.Equals(name, StringComparison.Ordinal) &&
            (returnType == null
                ? method.ReturnType != typeof(void)
                : method.ReturnType == returnType) &&
            method.GetParameters() is [{ ParameterType: var parameterType }] &&
            AcceptsCardModel(parameterType));

    private static MethodInfo? FindNodeMethod(Type type, string name, Type returnType) =>
        type.GetMethods(AllStatic).FirstOrDefault(method =>
            method.Name.Equals(name, StringComparison.Ordinal) &&
            method.ReturnType == returnType &&
            method.GetParameters() is [{ ParameterType: var parameterType }] &&
            (parameterType == typeof(NCard) || parameterType.IsAssignableFrom(typeof(NCard))));

    private static bool AcceptsCardModel(Type parameterType) =>
        parameterType == typeof(CardModel) || parameterType.IsAssignableFrom(typeof(CardModel));

    private static bool IsFrameworkAssembly(Assembly assembly)
    {
        if (assembly == typeof(ExternalCardVisualBridge).Assembly ||
            assembly == typeof(CardModel).Assembly)
        {
            return true;
        }

        var name = assembly.GetName().Name ?? string.Empty;
        return name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("netstandard", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("GodotSharp", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("0Harmony", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("System", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase);
    }

    private static RegistryProbe? CreateRegistryProbe(
        IReadOnlyCollection<Type> types,
        string registryTypeName,
        string methodName)
    {
        var registryType = types.FirstOrDefault(type =>
            type.Name.Equals(registryTypeName, StringComparison.Ordinal));
        if (registryType == null)
        {
            return null;
        }

        foreach (var method in registryType.GetMethods(AllStatic).Where(method =>
                     method.Name.Equals(methodName, StringComparison.Ordinal) &&
                     method.ReturnType == typeof(bool)))
        {
            var parameters = method.GetParameters();
            if (parameters.Length != 2 ||
                parameters[0].ParameterType != typeof(string) ||
                !parameters[1].ParameterType.IsByRef)
            {
                continue;
            }

            var stateType = parameters[1].ParameterType.GetElementType();
            var hasAnyValue = stateType?.GetMethods(AllInstance)
                .FirstOrDefault(candidate =>
                    candidate.Name.Equals("HasAnyValue", StringComparison.Ordinal) &&
                    candidate.ReturnType == typeof(bool) &&
                    candidate.GetParameters().Length == 0);
            if (stateType != null && hasAnyValue != null)
            {
                return new RegistryProbe(method, hasAnyValue);
            }
        }

        return null;
    }

    private static Type[] GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type != null).Cast<Type>().ToArray();
        }
    }

    private static void LogFailure(Assembly assembly, string operation, Exception exception)
    {
        var key = assembly.FullName + "\n" + operation + "\n" + exception.GetType().FullName;
        lock (Sync)
        {
            if (!LoggedFailures.Add(key))
            {
                return;
            }
        }

        ModLog.Warn(
            $"外部卡牌视觉管理器 {assembly.GetName().Name} 的{operation}失败，将由皮肤切换器继续呈现：" +
            exception.GetBaseException().Message);
    }

    private const BindingFlags AllStatic =
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags AllInstance =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private interface ICardVisualOwnershipAdapter
    {
        ExternalCardVisualOwnership GetOwnership(CardModel card);
    }

    private sealed class CardEditorAdapter(
        Assembly assembly,
        MethodInfo resolveIdentity,
        MethodInfo hasPortrait,
        RegistryProbe? frameProbe,
        RegistryProbe? textProbe) : ICardVisualOwnershipAdapter
    {
        public Assembly Assembly { get; } = assembly;

        public ExternalCardVisualOwnership GetOwnership(CardModel card)
        {
            string? cardId;
            try
            {
                cardId = resolveIdentity.Invoke(null, [card]) as string;
            }
            catch (Exception exception)
            {
                LogFailure(Assembly, "卡牌标识读取", exception);
                return default;
            }
            if (string.IsNullOrWhiteSpace(cardId))
            {
                return default;
            }

            var portrait = false;
            try
            {
                portrait = hasPortrait.Invoke(null, [card, cardId]) is true;
            }
            catch (Exception exception)
            {
                LogFailure(Assembly, "卡图状态读取", exception);
            }

            return new ExternalCardVisualOwnership(
                portrait,
                HasRegistryOverride(cardId, frameProbe, "卡框状态读取"),
                HasRegistryOverride(cardId, textProbe, "文字状态读取"));
        }

        private bool HasRegistryOverride(
            string cardId,
            RegistryProbe? probe,
            string operation)
        {
            if (probe == null)
            {
                return false;
            }

            try
            {
                return probe.HasOverride(cardId);
            }
            catch (Exception exception)
            {
                LogFailure(Assembly, operation, exception);
                return false;
            }
        }
    }

    private sealed class FullCardPresentationAdapter(
        Assembly assembly,
        MethodInfo hasPresentation,
        MethodInfo isEnabled,
        MethodInfo getCurrent) : ICardVisualOwnershipAdapter
    {
        public ExternalCardVisualOwnership GetOwnership(CardModel card)
        {
            try
            {
                if (hasPresentation.Invoke(null, [card]) is true &&
                    isEnabled.Invoke(null, [card]) is true &&
                    getCurrent.Invoke(null, [card]) != null)
                {
                    return new ExternalCardVisualOwnership(true, true, true);
                }
            }
            catch (Exception exception)
            {
                LogFailure(assembly, "完整卡牌呈现状态读取", exception);
            }

            return default;
        }
    }

    private sealed class RegistryProbe(MethodInfo tryGetOverride, MethodInfo hasAnyValue)
    {
        public bool HasOverride(string cardId)
        {
            object?[] arguments = [cardId, null];
            if (tryGetOverride.Invoke(null, arguments) is not true || arguments[1] == null)
            {
                return false;
            }

            return hasAnyValue.Invoke(arguments[1], null) is true;
        }
    }
}

internal readonly record struct ExternalCardVisualOwnership(
    bool Portrait,
    bool Frame,
    bool Text)
{
    public ExternalCardVisualOwnership Merge(ExternalCardVisualOwnership other) =>
        new(Portrait || other.Portrait, Frame || other.Frame, Text || other.Text);
}
