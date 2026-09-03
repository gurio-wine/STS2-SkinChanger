namespace STS2SkinChanger.Ui;

internal static class DraggableControlPlacementPolicy
{
    internal static readonly NormalizedControlPosition CharacterMergeDefault =
        new(0.1375f, 0.75925916f);

    internal static readonly NormalizedControlPosition CharacterBundleDefault =
        new(0.13007812f, 0.8090278f);

    internal static NormalizedControlPosition ClampNormalized(
        float x, float y, float viewportWidth, float viewportHeight,
        float controlWidth, float controlHeight)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) ||
            !float.IsFinite(viewportWidth) || !float.IsFinite(viewportHeight) ||
            !float.IsFinite(controlWidth) || !float.IsFinite(controlHeight) ||
            viewportWidth <= 0f || viewportHeight <= 0f)
        {
            return new NormalizedControlPosition(0.5f, 0.5f);
        }

        var halfWidth = Math.Clamp(controlWidth / 2f / viewportWidth, 0f, 0.5f);
        var halfHeight = Math.Clamp(controlHeight / 2f / viewportHeight, 0f, 0.5f);
        return new NormalizedControlPosition(
            Math.Clamp(x, halfWidth, 1f - halfWidth),
            Math.Clamp(y, halfHeight, 1f - halfHeight));
    }
}
