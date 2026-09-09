using System.Text.Json;
using STS2SkinChanger.Core;

internal static class WorkshopReplySortTests
{
    internal static void Run()
    {
        var metadata = new Dictionary<ulong, WorkshopDetails>
        {
            [11] = new("A", "") { Subscriptions = 10 },
            [22] = new("B", "") { Subscriptions = 0 },
            [33] = new("C", "") { Subscriptions = 20 }
        };
        var floors = new Dictionary<ulong, int> { [11] = 0, [22] = 3, [33] = 17, [66] = 17 };
        ulong[] Sort(WorkshopSort sort, bool reverse = false) => WorkshopSortPolicy.OrderIds(
            [55, 66, 33, 22, 44, 11], metadata, sort, reverse, floors);
        Require(Sort(WorkshopSort.LatestReply).SequenceEqual(new ulong[] { 33, 66, 22, 11, 44, 55 }),
            "最新楼层应在前，主帖在回复之后，无楼层条目视为最旧，同楼层次序稳定");
        Require(Sort(WorkshopSort.LatestReply, true).SequenceEqual(new ulong[] { 44, 55, 11, 22, 33, 66 }),
            "倒序应从无楼层、主帖、旧回复到新回复");
        Require(Sort(WorkshopSort.Subscriptions).SequenceEqual(new ulong[] { 33, 11, 22, 44, 55, 66 }),
            "默认正序不能改变订阅最多的含义");
        Require(Sort(WorkshopSort.Subscriptions, true).SequenceEqual(new ulong[] { 22, 11, 33, 44, 55, 66 }),
            "订阅倒序从已知零开始，未知统计不能冒充零；同值保持稳定");
        Require(WorkshopSortPolicy.OrderIds([22, 11], metadata, WorkshopSort.LatestReply).SequenceEqual(new ulong[] { 11, 22 }),
            "离线旧缓存尚无楼层数据时列表也要保持稳定可用");
        Require(WorkshopSortPolicy.Metric(null, WorkshopSort.LatestReply, "zhs", 17).Contains("17") &&
                WorkshopSortPolicy.Metric(null, WorkshopSort.LatestReply, "zhs", 0).Contains("0") &&
                WorkshopSortPolicy.Metric(metadata[33], WorkshopSort.LatestReply, "zhs") == "—",
            "楼层指标不依赖Steam统计加载，非楼层项不能冒充主帖");
        foreach (var sort in Enum.GetValues<WorkshopSort>().Where(s => s != WorkshopSort.LatestReply))
        {
            var values = new Dictionary<ulong, WorkshopDetails>
            {
                [1] = new("A", "") { Subscriptions=30, LifetimeSubscriptions=30, Score=.9f, Created=300, Updated=300, Comments=30, Favorites=30 },
                [2] = new("B", "") { Subscriptions=10, LifetimeSubscriptions=10, Score=.1f, Created=100, Updated=100, Comments=10, Favorites=10 }
            };
            Require(WorkshopSortPolicy.OrderIds([1, 2], values, sort, true).SequenceEqual(new ulong[] { 2, 1 }),
                $"倒序选择没有作用到排序模式 {sort}");
        }

        var item = new WorkshopCatalogItem(33, [new("cards", "silent")]);
        WorkshopCodeCandidate Candidate(int floor, int sequence) => new(new(3, 2868840, "1", "0.111", item),
            new string('A', 24), [new("test", WorkshopDiscussionSource.Url, 1, floor, sequence)]);
        var state = WorkshopSubmissionIntegrity.Verify(new([Candidate(3, 0), Candidate(17, 1)], []),
            new Dictionary<ulong, WorkshopIdentity> { [33] = new(2868840, "Test") }, WorkshopCommunityState.Empty);
        var saved = JsonSerializer.Serialize(state);
        using var json = JsonDocument.Parse(saved);
        Require(json.RootElement.GetProperty("Entries")[0].GetProperty("Reply").GetInt32() == 17,
            "最终采用的有效投稿楼层必须写入缓存，而不是按工坊物品发布时间猜测");
        var restored = JsonSerializer.Deserialize<WorkshopCommunityState>(saved)!;
        Require(JsonSerializer.Serialize(restored) == saved, "楼层缓存需可往返恢复");
        var legacy = JsonSerializer.Deserialize<WorkshopVerifiedSubmission>("""
            {"Item":{"Id":33,"Targets":[],"RestartRequired":true},"Name":"Old","Group":"AAAAAAAAAAAAAAAAAAAAAAAA"}
            """)!;
        Require(legacy.Reply == null,
            "旧缓存没有楼层时不得伪造主帖楼层");
        Console.WriteLine("Workshop reply sorting passed: source floor, cache compatibility, both directions and stable unknown/tied values.");
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
