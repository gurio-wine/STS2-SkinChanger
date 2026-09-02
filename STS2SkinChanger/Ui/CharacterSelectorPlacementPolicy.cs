namespace STS2SkinChanger.Ui;

internal enum CharacterSelectorHost
{
    InfoPanel,
    Screen
}

internal readonly record struct CharacterSelectorPlacement(
    CharacterSelectorHost Host,
    float AnchorLeft,
    float AnchorTop,
    float AnchorRight,
    float AnchorBottom,
    float OffsetLeft,
    float OffsetTop,
    float OffsetRight,
    float OffsetBottom);

internal static class CharacterSelectorPlacementPolicy
{
    internal static CharacterSelectorPlacement Resolve(bool useTopRight) =>
        useTopRight
            ? new CharacterSelectorPlacement(
                CharacterSelectorHost.Screen,
                AnchorLeft: 1f,
                AnchorTop: 0f,
                AnchorRight: 1f,
                AnchorBottom: 0f,
                OffsetLeft: -500f,
                OffsetTop: 92f,
                OffsetRight: -48f,
                OffsetBottom: 136f)
            : new CharacterSelectorPlacement(
                CharacterSelectorHost.InfoPanel,
                AnchorLeft: 0.5f,
                AnchorTop: 0f,
                AnchorRight: 0.5f,
                AnchorBottom: 0f,
                OffsetLeft: -226f,
                OffsetTop: -80f,
                OffsetRight: 226f,
                OffsetBottom: -36f);
}
