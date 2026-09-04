namespace STS2SkinChanger.Core;

internal static class ManagedProviderDisplayPolicy
{
    public static bool IsManaged(
        string? modId,
        IReadOnlyCollection<string> providerIds)
    {
        // A formal snapshot can be displayed through its Steam descriptor. Match its manifest
        // ID, not the shared folder, which may also contain unrelated gameplay/utility mods.
        return !string.IsNullOrWhiteSpace(modId) &&
               providerIds.Contains(modId, StringComparer.OrdinalIgnoreCase);
    }
}
