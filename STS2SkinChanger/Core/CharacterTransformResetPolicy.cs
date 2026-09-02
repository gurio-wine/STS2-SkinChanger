namespace STS2SkinChanger.Core;

internal static class CharacterTransformResetPolicy
{
    private const float Epsilon = 0.0001f;

    public static bool NeedsModelReset(CharacterCombatTransform value) =>
        MathF.Abs(value.Scale - 1f) > Epsilon ||
        MathF.Abs(value.OffsetX) > Epsilon ||
        MathF.Abs(value.OffsetY) > Epsilon;

    public static CharacterCombatTransform ResetModel(CharacterCombatTransform value) =>
        value with
        {
            Scale = 1f,
            OffsetX = 0f,
            OffsetY = 0f
        };
}
