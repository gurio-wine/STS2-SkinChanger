namespace STS2SkinChanger.Ui;

internal readonly record struct NormalizedControlPosition(float X, float Y);

internal static class CardSkinSelectorPlacementPolicy
{
    public const float SelectorWidth = 336f;
    public const float SelectorHeight = 48f;
    public const float ReferenceViewportHeight = 1200f;

    // The original selector was centred horizontally and occupied y=74..122 on the
    // reference 1200px-tall viewport, so its centre is y=98.
    public static readonly NormalizedControlPosition DefaultPosition =
        new(0.5f, 98f / ReferenceViewportHeight);

    public static NormalizedControlPosition ResolveStored(float? storedX, float? storedY)
    {
        if (storedX is not { } x || storedY is not { } y ||
            !float.IsFinite(x) || !float.IsFinite(y))
        {
            return DefaultPosition;
        }

        return new NormalizedControlPosition(
            Math.Clamp(x, 0f, 1f),
            Math.Clamp(y, 0f, 1f));
    }

    public static NormalizedControlPosition ClampNormalized(
        float requestedX,
        float requestedY,
        float viewportWidth,
        float viewportHeight)
    {
        if (!float.IsFinite(requestedX) || !float.IsFinite(requestedY) ||
            !float.IsFinite(viewportWidth) || !float.IsFinite(viewportHeight) ||
            viewportWidth <= 0f || viewportHeight <= 0f)
        {
            return DefaultPosition;
        }

        var halfWidth = Math.Min(0.5f, SelectorWidth / 2f / viewportWidth);
        var halfHeight = Math.Min(0.5f, SelectorHeight / 2f / viewportHeight);
        return new NormalizedControlPosition(
            Math.Clamp(requestedX, halfWidth, 1f - halfWidth),
            Math.Clamp(requestedY, halfHeight, 1f - halfHeight));
    }
}
