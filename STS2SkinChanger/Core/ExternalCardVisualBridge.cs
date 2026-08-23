using MegaCrit.Sts2.Core.Models;
using System.Reflection;

namespace STS2SkinChanger.Core;

/// <summary>
/// Lets live card editors keep ownership of layers that the player explicitly edited.
/// Discovery is capability based so the bridge is not tied to a workshop id or install path.
/// </summary>
internal static class ExternalCardVisualBridge
{
    private static readonly object Sync = new();
    private static readonly List<CardEditorAdapter> Adapters = [];
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

    private static CardEditorAdapter[] EnumerateAdapters()
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
                if (identityType == null || portraitResolverType == null)
                {
                    continue;
                }

                var resolveIdentity = identityType.GetMethods(AllStatic)
                    .FirstOrDefault(method =>
                        method.Name.Equals("Resolve", StringComparison.Ordinal) &&
                        method.ReturnType == typeof(string) &&
                        method.GetParameters() is [{ ParameterType: var parameterType }] &&
                        (parameterType == typeof(CardModel) ||
                         parameterType.IsAssignableFrom(typeof(CardModel))));
                var hasPortrait = portraitResolverType.GetMethods(AllStatic)
                    .FirstOrDefault(method =>
                        method.Name.Equals("HasAnyPortraitReplacement", StringComparison.Ordinal) &&
                        method.ReturnType == typeof(bool) &&
                        method.GetParameters() is
                        [
                            { ParameterType: var cardType },
                            { ParameterType: var idType }
                        ] &&
                        (cardType == typeof(CardModel) ||
                         cardType.IsAssignableFrom(typeof(CardModel))) &&
                        idType == typeof(string));
                if (resolveIdentity == null || hasPortrait == null)
                {
                    continue;
                }

                var frameProbe = CreateRegistryProbe(
                    types,
                    "CardFrameOverrideRegistry",
                    "TryGetOverride");
                var textProbe = CreateRegistryProbe(
                    types,
                    "CardTextOverrideRegistry",
                    "TryGetOverride");
                var adapter = new CardEditorAdapter(
                    assembly,
                    resolveIdentity,
                    hasPortrait,
                    frameProbe,
                    textProbe);
                Adapters.Add(adapter);
                ModLog.Info(
                    $"检测到外部卡牌视觉管理器 {assembly.GetName().Name}；" +
                    "它明确编辑的卡图、卡框和文字层将优先于皮肤切换器。");
            }
            catch (Exception exception)
            {
                LogFailure(assembly, "能力探测", exception);
            }
        }
    }

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

    private sealed class CardEditorAdapter(
        Assembly assembly,
        MethodInfo resolveIdentity,
        MethodInfo hasPortrait,
        RegistryProbe? frameProbe,
        RegistryProbe? textProbe)
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
