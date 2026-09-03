namespace STS2SkinChanger.Ui;

internal static class PauseMenuAppearanceClickPolicy
{
    internal static bool ShouldToggleVisibility(bool isRightButton, bool pressed) =>
        isRightButton && pressed;
}
