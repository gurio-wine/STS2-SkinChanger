using STS2SkinChanger.Core;

internal static class WorkshopPagePoolTests
{
    public static void Run()
    {
        var created = 0;
        var pool = new WorkshopPagePool<object>(8, _ => { created++; return new object(); });
        void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
        pool.Bind([]);
        Require(created == 0, "空筛选不能创建没有使用的物品行。");
        pool.Bind([1, 2]);
        var first = pool.Slots[0];
        var second = pool.Slots[1];
        var oldCover = first.Binding.Capture();
        Require(created == 2 && first.Binding.Matches(oldCover), "首次仅创建需要的行，当前封面可更新。");
        pool.Bind([3, 4, 5]);
        Require(created == 3 && ReferenceEquals(first.View, pool.Slots[0].View) &&
            ReferenceEquals(second.View, pool.Slots[1].View), "翻页须复用原有视图，只补足缺少的槽位。");
        Require(!first.Binding.Matches(oldCover) && first.Binding.Id == 3, "上一页的下载结果或按下后延迟的点击不能落到下一页。");
        pool.Bind([1]);
        Require(!first.Binding.Matches(oldCover), "翻回同一物品也不能接受旧一轮的回调。");
        Require(pool.Slots.Skip(1).All(slot => slot.Binding.Id == 0) && !second.Binding.Matches(second.Binding.Capture()),
            "少项/空页的闲置槽位必须不可操作。");
        var current = first.Binding.Capture();
        pool.Bind([1]);
        Require(!first.Binding.Matches(current), "同 ID 的筛选上下文改变也要使旧标签点击失效。");
        for (var page = 0; page < 100; page++)
            pool.Bind(Enumerable.Range(page * 8 + 1, 8).Select(id => (ulong)id).ToArray());
        Require(created == 8 && pool.Slots.Count == 8, "翻阅一百页仍只能保留一页控件，不能无限缓存整个目录。");
        var last = pool.Slots[0].Binding.Capture();
        pool.Bind([]);
        Require(!pool.Slots[0].Binding.Matches(last) && pool.Slots.All(slot => slot.Binding.Id == 0), "清空列表须撤销全部活动绑定。");
        Console.WriteLine("Workshop page pool passed: bounded reuse, empty pages, stale images and stale clicks.");
    }
}
