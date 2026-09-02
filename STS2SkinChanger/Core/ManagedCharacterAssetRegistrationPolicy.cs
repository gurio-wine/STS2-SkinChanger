namespace STS2SkinChanger.Core;

internal static class ManagedCharacterAssetRegistrationPolicy
{
    public static bool ShouldSuppress(
        string? registryModId,
        IReadOnlySet<string> managedProviderIds) =>
        !string.IsNullOrWhiteSpace(registryModId) &&
        managedProviderIds.Contains(registryModId);
}
