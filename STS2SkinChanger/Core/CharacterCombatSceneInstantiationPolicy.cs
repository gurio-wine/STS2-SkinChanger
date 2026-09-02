namespace STS2SkinChanger.Core;

internal static class CharacterCombatSceneInstantiationPolicy
{
    public static bool ShouldUseManagedFactory(
        bool isBaseSelection,
        bool hasManagedCombatScene,
        bool hasManagedCombatDependencies) =>
        isBaseSelection || hasManagedCombatScene || hasManagedCombatDependencies;

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
