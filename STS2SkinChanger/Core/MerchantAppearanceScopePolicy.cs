namespace STS2SkinChanger.Core;

/// <summary>
/// Which part of a shop a style/UI package's own drawing belongs to.
///
/// A merchant appearance is the merchant's own figure and its hand. The room the merchant stands in
/// and the shop interface it opens are the package's whole point — those stay exactly as the author
/// drew them, no matter which merchant skin the player picked, and are therefore never bound to the
/// merchant selection. Type and node names only, so the rule can be verified without a game.
/// </summary>
internal static class MerchantAppearanceScopePolicy
{
    private const string MerchantButtonToken = "MerchantButton";
    private const string MerchantHandToken = "MerchantHand";
    private const string ShopInterfaceToken = "MerchantInventory";
    private const string RoomToken = "MerchantRoom";
    private const string FakeMerchantToken = "FakeMerchant";
    private const string BackgroundNodeName = "BgContainer";

    /// <summary>
    /// Whether a node's own art is part of the merchant appearance the selection owns. The ancestry
    /// is walked from the node itself to the root of the shop: the nearest entry decides, so the
    /// hand inside the inventory and the merchant figure inside the room are still the merchant's,
    /// while the room's background and the shop screens are not.
    /// </summary>
    internal static bool IsMerchantAppearance(IEnumerable<(string TypeName, string NodeName)> ancestry)
    {
        ArgumentNullException.ThrowIfNull(ancestry);
        foreach (var (typeName, nodeName) in ancestry)
        {
            if (Contains(typeName, MerchantHandToken) || Contains(typeName, MerchantButtonToken))
            {
                return true;
            }

            if (Contains(typeName, ShopInterfaceToken) ||
                Contains(typeName, RoomToken) ||
                Contains(typeName, FakeMerchantToken) ||
                nodeName.Equals(BackgroundNodeName, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return false;
    }

    private static bool Contains(string? typeName, string token) =>
        typeName != null && typeName.Contains(token, StringComparison.OrdinalIgnoreCase);
}
