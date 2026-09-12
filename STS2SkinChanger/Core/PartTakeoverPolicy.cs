using System.Reflection;

namespace STS2SkinChanger.Core;

/// <summary>
/// One part a style/UI package overrides inside a group the game already presents. The package is
/// not a skin: it keeps loading through the game's own loader, and this record is what the group's
/// selection uses to decide whether the package's own presentation for that group is in effect.
/// </summary>
internal sealed record PartTakeoverOwner(
    string GroupId,
    string OptionId,
    string? RootPath,
    IReadOnlySet<string> ResourceNamespaces)
{
    /// <summary>
    /// The word a game type carries when it belongs to this group's presentation. A package's
    /// merchant module patches NMerchantRoom/NMerchantInventory, so the gate stays on the group the
    /// part belongs to instead of on any Mod's name, ID or author namespace.
    /// </summary>
    public string GroupToken => GroupId;

    public bool OwnsResourcePath(string? resourcePath)
    {
        if (string.IsNullOrWhiteSpace(resourcePath))
        {
            return false;
        }

        const string Prefix = "res://";
        var path = resourcePath.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            ? resourcePath[Prefix.Length..]
            : resourcePath;
        var separator = path.IndexOf('/');
        var head = separator < 0 ? path : path[..separator];
        return head.Length > 0 && ResourceNamespaces.Contains(head);
    }
}

/// <summary>How one of the package's callbacks was attached to the group's own game method.</summary>
internal enum PartTakeoverPatchKind
{
    Prefix,
    Postfix,
    Transpiler,
    Finalizer
}

/// <summary>
/// One callback the package installed on a game method of the group it overrides. The package
/// presents that group from this callback, so the selection can both hold it back and repeat it.
/// </summary>
internal sealed record PartTakeoverPatch(
    MethodBase Target,
    MethodInfo Callback,
    PartTakeoverPatchKind Kind,
    PartTakeoverOwner Owner);

/// <summary>
/// Decision rules for a part takeover, kept free of engine and patch dependencies so they can be
/// verified without a game: which packages have a part taken over, which resource namespaces their
/// own art appears under, which of their patches belong to the group they override, and which of
/// those patches may be repeated on a scene that is already standing.
/// </summary>
internal static class PartTakeoverPolicy
{
    internal static IReadOnlyList<PartTakeoverOwner> ResolveOwners(
        IEnumerable<(string GroupId, string OptionId, string? RootPath, bool IsBaselinePartOverride)> options)
    {
        var owners = new List<PartTakeoverOwner>();
        foreach (var option in options)
        {
            if (!option.IsBaselinePartOverride ||
                string.IsNullOrWhiteSpace(option.GroupId) ||
                string.IsNullOrWhiteSpace(option.OptionId))
            {
                continue;
            }

            owners.Add(new PartTakeoverOwner(
                option.GroupId,
                option.OptionId,
                option.RootPath,
                ResolveResourceNamespaces(option.OptionId, [])));
        }

        return owners;
    }

    /// <summary>
    /// The namespaces a package's own art can appear under. Its option ID is the manifest ID the
    /// publisher used and its DLL file name is the other name its resources are exported with;
    /// both are read from disk rather than assumed to be equal.
    /// </summary>
    internal static IReadOnlySet<string> ResolveResourceNamespaces(
        string optionId,
        IEnumerable<string> assemblyNames)
    {
        var namespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { optionId };
        foreach (var assemblyName in assemblyNames)
        {
            var name = Path.GetFileNameWithoutExtension(assemblyName);
            if (!string.IsNullOrWhiteSpace(name))
            {
                namespaces.Add(name);
            }
        }

        return namespaces;
    }

    /// <summary>
    /// Whether a patch callback belongs to the group's own presentation. Only the group's game
    /// types qualify, so a package's unrelated patches keep running.
    /// </summary>
    internal static bool TargetsGroupPresentation(PartTakeoverOwner owner, Type? declaringType) =>
        declaringType != null &&
        declaringType.Name.Contains(owner.GroupToken, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the namespace a resource path is exported under. Only a package that already had a
    /// part taken over can own a path here, so an unrelated Mod's art is never touched.
    /// </summary>
    internal static PartTakeoverOwner? ResolveOwner(
        string? resourcePath,
        IReadOnlyList<PartTakeoverOwner> owners) =>
        string.IsNullOrWhiteSpace(resourcePath)
            ? null
            : owners.FirstOrDefault(owner => owner.OwnsResourcePath(resourcePath));

    /// <summary>Whether two records describe the same selectable part of the same group.</summary>
    internal static bool SameOption(PartTakeoverOwner left, PartTakeoverOwner right) =>
        left.GroupId.Equals(right.GroupId, StringComparison.OrdinalIgnoreCase) &&
        left.OptionId.Equals(right.OptionId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this game method is where a scene presents itself for the first time, i.e. the place
    /// a package attaches its own version of that scene. A scene that is already standing can never
    /// run it again, so this is exactly what a selection change repeats on the live scene.
    /// </summary>
    internal static bool IsPresentationInitializer(MethodBase target) =>
        target.GetParameters().Length == 0 &&
        target.Name.Equals("_Ready", StringComparison.Ordinal);

    /// <summary>
    /// The package callbacks that draw the given scene, in a stable order. Only postfixes of that
    /// scene's own initialization qualify: a prefix can suppress or decide game behavior, which a
    /// repeated selection must never re-run outside the game's own call.
    /// </summary>
    internal static IReadOnlyList<PartTakeoverPatch> ResolveReplayablePresentation(
        IEnumerable<PartTakeoverPatch> patches,
        PartTakeoverOwner owner,
        Type sceneType) =>
        [.. patches
            .Where(patch =>
                patch.Kind == PartTakeoverPatchKind.Postfix &&
                SameOption(patch.Owner, owner) &&
                patch.Target.DeclaringType?.IsAssignableFrom(sceneType) == true &&
                IsPresentationInitializer(patch.Target))
            .OrderBy(patch => patch.Target.DeclaringType?.FullName, StringComparer.Ordinal)
            .ThenBy(patch => patch.Callback.DeclaringType?.FullName, StringComparer.Ordinal)
            .ThenBy(patch => patch.Callback.Name, StringComparer.Ordinal)];

    /// <summary>
    /// The arguments one recorded callback can be repeated with. Game parameters a postfix reads by
    /// name, or a result it expects from the skipped original, cannot be reconstructed and make the
    /// callback ineligible instead of being guessed.
    /// </summary>
    internal static bool TryBuildPresentationArguments(
        MethodInfo callback,
        MethodBase target,
        object scene,
        out object?[] arguments)
    {
        arguments = [];
        if (!callback.IsStatic)
        {
            return false;
        }

        var parameters = callback.GetParameters();
        var built = new object?[parameters.Length];
        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            var parameterType = parameter.ParameterType.IsByRef
                ? parameter.ParameterType.GetElementType()!
                : parameter.ParameterType;
            switch (parameter.Name)
            {
                case "__instance" when parameterType.IsInstanceOfType(scene):
                    built[index] = scene;
                    break;
                case "__originalMethod" when parameterType == typeof(MethodBase):
                    built[index] = target;
                    break;
                case "__runOriginal" when parameterType == typeof(bool):
                    built[index] = true;
                    break;
                // A postfix paired with a prefix receives a fresh neutral state: the prefix belongs
                // to the game's own call, so only the callback's own drawing is repeated.
                case "__state":
                    built[index] = parameterType.IsValueType
                        ? Activator.CreateInstance(parameterType)
                        : null;
                    break;
                default:
                    if (parameter.HasDefaultValue)
                    {
                        built[index] = parameter.DefaultValue;
                        break;
                    }

                    return false;
            }
        }

        arguments = built;
        return true;
    }
}
