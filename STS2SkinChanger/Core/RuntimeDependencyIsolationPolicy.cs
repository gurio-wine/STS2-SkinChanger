namespace STS2SkinChanger.Core;

internal static class RuntimeDependencyIsolationPolicy
{
    public static bool CanReuseMountedProviderDependency(
        bool belongsToSelectedProvider,
        bool isProviderExclusivePath,
        bool isMountedBySelectedOverlay) =>
        belongsToSelectedProvider &&
        isProviderExclusivePath &&
        isMountedBySelectedOverlay;
}
