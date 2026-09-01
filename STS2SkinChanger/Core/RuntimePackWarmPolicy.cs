namespace STS2SkinChanger.Core;

internal static class RuntimePackWarmPolicy
{
    internal const long MaximumWarmPackBytes = 64L * 1024L * 1024L;

    public static bool ShouldWarm(long sizeBytes, bool alreadyWarmed) =>
        !alreadyWarmed &&
        sizeBytes > 0L &&
        sizeBytes <= MaximumWarmPackBytes;
}
