namespace STS2SkinChanger.Ui;

internal enum MerchantProviderReadyTarget
{
    Button,
    Hand,
    Inventory
}

internal enum MerchantProviderPostfixTiming
{
    Immediate,
    NextFrameThenSpineReady
}

/// <summary>
/// Keeps provider _Ready postfixes aligned with the native merchant lifecycle. A provider prefix
/// may replace a Spine resource after the scene node already exists; waiting one frame prevents a
/// postfix from binding state to the outgoing skeleton even when that old skeleton still reports
/// itself ready.
/// </summary>
internal static class MerchantProviderReadyPolicy
{
    internal static MerchantProviderPostfixTiming ResolvePostfixTiming(
        MerchantProviderReadyTarget target) =>
        target is MerchantProviderReadyTarget.Button or MerchantProviderReadyTarget.Hand
            ? MerchantProviderPostfixTiming.NextFrameThenSpineReady
            : MerchantProviderPostfixTiming.Immediate;

    internal static bool ShouldRefreshFocusAfterProviderReady(bool isFocused) => isFocused;
}
