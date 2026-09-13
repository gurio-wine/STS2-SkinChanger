using STS2SkinChanger.Core;

internal static class GitHubSourceRoutingTests
{
    public static void Run()
    {
        void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
        var steam = new WorkshopCatalogItem(3787302680UL, [new("character", "silent")]);
        var github = new WorkshopCatalogItem(SkinSourceIdentity.GitHubId("o", "r"), [new("cards", "silent")]);
        var union = SkinSourceRoutingPolicy.CatalogUnion([steam], [github]);
        Require(union.Length == 2 && union[0].Id == steam.Id, "并集必须同时包含两种来源，Steam 条目在前保持既有顺序。");
        var duplicate = new WorkshopCatalogItem(steam.Id, [new("cards", "defect")]);
        Require(SkinSourceRoutingPolicy.CatalogUnion([steam], [duplicate]).Single().Targets.Single().Kind == "character",
            "同一 ID 冲突时以 Steam 条目为准；合成 ID 不会与工坊 ID 相同，冲突只可能来自坏数据。");
        Require(SkinSourceRoutingPolicy.IsSteamItem(steam.Id) && !SkinSourceRoutingPolicy.IsSteamItem(github.Id),
            "Steam 专用路径必须只对真实工坊 ID 生效。");
        Require(SkinSourceRoutingPolicy.IsSteamItem(0) == false && SkinSourceRoutingPolicy.IsSteamItem(SkinSourceIdentity.GitHubMask) == false,
            "空 ID 与只有来源位的合成 ID 都不是工坊物品。");
        Require(SkinSourceRoutingPolicy.CatalogUnion([], [github]).Single().Id == github.Id,
            "只有 GitHub 条目时并集也必须给出它们。");
        Console.WriteLine("GitHub source routing passed: union catalog and Steam-only guards.");
    }
}
