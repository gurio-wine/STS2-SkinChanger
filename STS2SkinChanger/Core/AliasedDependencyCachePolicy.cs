namespace STS2SkinChanger.Core;

internal readonly record struct AliasedDependencyReference(
    bool IsRewritable,
    IReadOnlyCollection<string> ResourcePaths);

internal static class AliasedDependencyCachePolicy
{
    public static bool CanReuseExternalDependencies(
        IEnumerable<AliasedDependencyReference> references,
        IEnumerable<string> aliasedSourcePaths)
    {
        var aliases = aliasedSourcePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in references)
        {
            if (reference.ResourcePaths.Count == 0)
            {
                continue;
            }

            // Text resources are rewritten to the per-selection alias namespace. Binary .res/.scn
            // payloads cannot be rewritten safely and still resolve their embedded public paths,
            // so they must retain deep cache isolation even when those public paths are bridged.
            if (!reference.IsRewritable ||
                reference.ResourcePaths.Any(path => !aliases.Contains(path)))
            {
                return false;
            }
        }

        return true;
    }
}
