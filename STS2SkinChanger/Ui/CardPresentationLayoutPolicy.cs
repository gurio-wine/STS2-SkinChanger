namespace STS2SkinChanger.Ui;

/// <summary>
/// Separates the game's Ancient card layout from a full-height alternate-art portrait.
/// Some card art providers reuse the AncientPortrait node only because it has the required
/// aspect ratio; that does not make the card an Ancient card.
/// </summary>
internal enum CardPresentationLayout
{
    Normal,
    ExpandedPortrait,
    Ancient
}

internal static class CardPresentationLayoutPolicy
{
    public static CardPresentationLayout Resolve(
        bool isNativeAncient,
        bool requestsAncientLayout,
        bool requestsExpandedPortrait)
    {
        if (isNativeAncient || requestsAncientLayout)
        {
            return CardPresentationLayout.Ancient;
        }

        return requestsExpandedPortrait
            ? CardPresentationLayout.ExpandedPortrait
            : CardPresentationLayout.Normal;
    }
}
