namespace STS2SkinChanger.Core;

internal static class CharacterCombatSceneInstantiationPolicy
{
    public static bool ShouldUseManagedFactory(
        bool isBaseSelection,
        bool hasManagedCombatScene,
        bool hasManagedCombatDependencies) =>
        isBaseSelection || hasManagedCombatScene || hasManagedCombatDependencies;

    /// <summary>
    /// The game's own initial creation (<c>CharacterModel.CreateVisuals</c>) reads the character's
    /// canonical combat scene path and hands the instantiated node straight to a
    /// NCreatureVisuals cast. A selection that serves that path itself must therefore own creation
    /// before the game runs: the served scene can be a framework scene (a plain Node2D that only
    /// the scene factory can convert) and the cast throws before any postfix could repair it.
    /// Base selection keeps the game's own instantiation, so untouched characters are never routed
    /// through the factory. The rebuild and preview paths deliberately use
    /// <see cref="ShouldUseManagedFactory"/> instead, where a restored base selection must not read
    /// a scoped skin's overlay.
    /// </summary>
    public static bool ShouldOwnCanonicalSceneInstantiation(
        bool isBaseSelection,
        bool servesCanonicalCombatScene) =>
        !isBaseSelection && servesCanonicalCombatScene;

    public static bool HasManagedCombatDependencies(
        string scenePath,
        IEnumerable<string> assetPaths)
    {
        var sceneStem = Path.GetFileNameWithoutExtension(scenePath);
        var combatDependencyPrefix = $"res://animations/characters/{sceneStem}/";
        return assetPaths.Any(path => path.StartsWith(
            combatDependencyPrefix,
            StringComparison.OrdinalIgnoreCase));
    }

    public static bool ShouldRestoreCanonicalOwnership(
        string? scopedSelection,
        string configuredSelection) =>
        scopedSelection != null &&
        !scopedSelection.Equals(configuredSelection, StringComparison.OrdinalIgnoreCase);
}
