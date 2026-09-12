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

/// <summary>
/// Decision rules for a part takeover, kept free of engine and patch dependencies so they can be
/// verified without a game: which packages have a part taken over, which resource namespaces their
/// own art appears under, and which of their patches belong to the group they override.
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
}
