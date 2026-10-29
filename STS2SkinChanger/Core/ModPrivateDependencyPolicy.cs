namespace STS2SkinChanger.Core;

/// <summary>
/// The game loads every Mod assembly with <c>AssemblyLoadContext.LoadFromAssemblyPath</c>, and that
/// load context only probes the game's own folder for dependencies; the game's single
/// <c>AssemblyResolve</c> fallback answers for <c>sts2</c> and <c>0Harmony</c> only. A Mod that
/// ships a private dependency beside its own DLL therefore cannot load it at runtime — the live
/// example is "3D Card Effects", whose <c>Microsoft.ML.OnnxRuntime.dll</c> never resolves, so its
/// depth generation for Mod card art always fails. These rules decide when such a dependency may
/// be resolved from the requesting Mod's own folder. Kept free of engine types so it is testable.
/// </summary>
internal static class ModPrivateDependencyPolicy
{
    /// <summary>
    /// Only code loaded from outside the game folder and outside the .NET runtime belongs to a Mod.
    /// Everything the game or the runtime itself ships keeps the default resolution it already had.
    /// </summary>
    public static bool IsModOwned(string? assemblyPath, string gameDirectory, string runtimeDirectory)
    {
        if (!TryFullPath(assemblyPath, out var path))
        {
            return false;
        }

        return !IsUnder(path, gameDirectory) && !IsUnder(path, runtimeDirectory);
    }

    /// <summary>
    /// The file the requesting Mod's own folder may provide for a dependency the loader could not
    /// resolve. Returns null when the request must keep failing: no Mod ownership, no such file, or
    /// a name the game itself ships.
    /// </summary>
    public static string? CandidatePath(
        string? requestingAssemblyPath,
        string? requestedAssemblyName,
        string gameDirectory,
        string runtimeDirectory,
        Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(requestedAssemblyName) ||
            !IsModOwned(requestingAssemblyPath, gameDirectory, runtimeDirectory) ||
            !TryFullPath(requestingAssemblyPath, out var requestingPath))
        {
            return null;
        }

        // Never shadow an assembly the game ships itself: a second copy with the same identity in
        // this process would break the game's own code.
        if (fileExists(Path.Combine(gameDirectory, requestedAssemblyName + ".dll")))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(requestingPath);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        var candidate = Path.Combine(directory, requestedAssemblyName + ".dll");
        return fileExists(candidate) ? candidate : null;
    }

    private static bool IsUnder(string path, string? root)
    {
        if (string.IsNullOrWhiteSpace(root) ||
            !TryFullPath(root, out var fullRoot))
        {
            return false;
        }

        return path.StartsWith(
            fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryFullPath(string? path, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(path);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
