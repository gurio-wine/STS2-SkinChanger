namespace STS2SkinChanger.Ui;

internal enum MerchantPreviewFocusEvent
{
    MouseEntered,
    MouseExited,
    ControllerFocused,
    ControllerUnfocused
}

internal readonly record struct MerchantPreviewFocusState(
    bool IsMouseHovered,
    bool IsControllerFocused)
{
    internal static MerchantPreviewFocusState None => new(false, false);

    internal bool IsFocused => IsMouseHovered || IsControllerFocused;
}

internal static class MerchantPreviewFocusPolicy
{
    internal static MerchantPreviewFocusState Resolve(
        MerchantPreviewFocusState current,
        MerchantPreviewFocusEvent focusEvent) =>
        focusEvent switch
        {
            MerchantPreviewFocusEvent.MouseEntered => current with { IsMouseHovered = true },
            MerchantPreviewFocusEvent.MouseExited => current with { IsMouseHovered = false },
            MerchantPreviewFocusEvent.ControllerFocused => current with { IsControllerFocused = true },
            MerchantPreviewFocusEvent.ControllerUnfocused => current with { IsControllerFocused = false },
            _ => current
        };
}
