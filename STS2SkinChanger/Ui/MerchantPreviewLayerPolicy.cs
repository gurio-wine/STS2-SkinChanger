namespace STS2SkinChanger.Ui;

internal enum MerchantPreviewBackButtonHost
{
    InventorySubViewport,
    CompendiumOverlay
}

internal readonly record struct MerchantPreviewLayerState(
    int PreviewZIndex,
    bool SkinSelectorVisible,
    bool ActionSelectorVisible,
    bool CompendiumBackEnabled,
    MerchantPreviewBackButtonHost NativeBackButtonHost);

internal static class MerchantPreviewLayerPolicy
{
    internal const int NormalPreviewZIndex = 0;
    internal const int HighestCompendiumOverlayZIndex = 11;
    private const int OpenInventoryPreviewZIndex = HighestCompendiumOverlayZIndex + 1;

    internal static MerchantPreviewLayerState Resolve(
        bool inventoryOpen,
        bool hasSkinOptions,
        bool actionSelectorRequested,
        bool compendiumVisible)
    {
        if (inventoryOpen)
        {
            return new MerchantPreviewLayerState(
                OpenInventoryPreviewZIndex,
                SkinSelectorVisible: false,
                ActionSelectorVisible: false,
                CompendiumBackEnabled: false,
                NativeBackButtonHost: MerchantPreviewBackButtonHost.CompendiumOverlay);
        }

        return new MerchantPreviewLayerState(
            NormalPreviewZIndex,
            SkinSelectorVisible: hasSkinOptions,
            ActionSelectorVisible: actionSelectorRequested,
            CompendiumBackEnabled: compendiumVisible,
            NativeBackButtonHost: MerchantPreviewBackButtonHost.InventorySubViewport);
    }
}
