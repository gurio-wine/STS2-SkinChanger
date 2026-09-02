namespace STS2SkinChanger.Core;

/// <summary>
/// Pure policy for deciding whether a skin framework dependency can be replaced by an
/// in-process compatibility contract.  The decision deliberately requires structural
/// evidence from the dependent skin DLL and its PCK; a Mod or framework name alone is never
/// enough to suppress executable code.
/// </summary>
internal static class OptionalSkinFrameworkPolicy
{
    public static bool ShouldTreatAsGameplayBaseline(
        bool manifestAffectsGameplay,
        bool requiredByAnotherMod,
        bool exposesSelectableCosmetics) =>
        manifestAffectsGameplay ||
        (requiredByAnotherMod && !exposesSelectableCosmetics);

    public static bool CanSatisfyMissingDependency(
        OptionalSkinFrameworkEvidence evidence,
        IReadOnlyCollection<string> compatibilityAssemblyNames) =>
        evidence.HasDeclarativeSkinContract &&
        evidence.ResourceClosureComplete &&
        !string.IsNullOrWhiteSpace(evidence.DependencyId) &&
        evidence.DependencyId.Equals(
            evidence.ReferencedAssemblyName,
            StringComparison.OrdinalIgnoreCase) &&
        compatibilityAssemblyNames.Contains(
            evidence.ReferencedAssemblyName,
            StringComparer.OrdinalIgnoreCase);

    public static bool IsFrameworkHostRequired(
        string frameworkId,
        IEnumerable<OptionalSkinFrameworkEvidence> dependents,
        IReadOnlyCollection<string> compatibilityAssemblyNames)
    {
        var matchingDependents = dependents
            .Where(evidence => evidence.DependencyId.Equals(
                frameworkId,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matchingDependents.Length == 0 ||
               matchingDependents.Any(evidence =>
                   !CanSatisfyMissingDependency(evidence, compatibilityAssemblyNames));
    }

    public static bool CanInstallCompatibilityAssembly(
        string frameworkId,
        IEnumerable<OptionalSkinFrameworkEvidence> dependents,
        bool originalFrameworkHostAvailable = false)
    {
        var evidence = dependents
            .Where(candidate => candidate.DependencyId.Equals(
                frameworkId,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (!evidence.Any(candidate =>
                CanSatisfyMissingDependency(candidate, [frameworkId])))
        {
            return false;
        }

        return !originalFrameworkHostAvailable ||
               !IsFrameworkHostRequired(frameworkId, evidence, [frameworkId]);
    }
}

internal sealed record OptionalSkinFrameworkEvidence(
    string DependentModId,
    string DependencyId,
    string ReferencedAssemblyName,
    bool HasDeclarativeSkinContract,
    bool ResourceClosureComplete);
