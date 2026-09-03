namespace STS2SkinChanger.Ui;

internal static class DraggableControlPlacementPolicy
{
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
