namespace STS2SkinChanger.Core;

// A release archive may hold several Mods. The one whose manifest sits at the archive root is
// the package the author published; a nested one is only picked when its folder carries the
// repository name. Pure string work on purpose: the descriptor type itself belongs to the game
// catalog, which the offline tests cannot load.
internal static class GitHubPackageLayout
{
    public static int SelectIndex(IReadOnlyList<string?> roots, string packageRoot, string repoName)
    {
        for (var index = 0; index < roots.Count; index++)
            if (Same(roots[index], packageRoot)) return index;
        for (var index = 0; index < roots.Count; index++)
        {
            var root = roots[index];
            if (root is { Length: > 0 } && Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .Equals(repoName, StringComparison.OrdinalIgnoreCase)) return index;
        }
        return roots.Count > 0 ? 0 : -1;
    }

    private static bool Same(string? left, string right) => left is { Length: > 0 } &&
        string.Equals(left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
}
