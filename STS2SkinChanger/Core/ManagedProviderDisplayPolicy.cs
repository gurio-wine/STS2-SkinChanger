namespace STS2SkinChanger.Core;

internal static class ManagedProviderDisplayPolicy
{
    public static bool IsManaged(
        string? modId,
        string? normalizedRoot,
        IReadOnlyCollection<string> providerRoots,
        IReadOnlyCollection<string> providerIds)
    {
        if (!string.IsNullOrWhiteSpace(normalizedRoot) &&
            providerRoots.Contains(normalizedRoot, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(modId) &&
               providerIds.Contains(modId, StringComparer.OrdinalIgnoreCase);
    }
}
