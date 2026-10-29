namespace STS2SkinChanger.Core;

// One identity space, two sources. Steam-only reads (subscription state, UGC metadata,
// download progress, hot registration) must be skipped for synthetic GitHub ids instead of
// being asked about an item Steam has never heard of. The catalog itself is simply the union.
internal static class SkinSourceRoutingPolicy
{
    public static bool IsSteamItem(ulong id) => SkinSourceIdentity.IsSteam(id);

    public static WorkshopCatalogItem[] CatalogUnion(
        IReadOnlyList<WorkshopCatalogItem> steam, IReadOnlyList<WorkshopCatalogItem> github) =>
        steam.Concat(github).GroupBy(item => item.Id).Select(group => group.First()).ToArray();
}
