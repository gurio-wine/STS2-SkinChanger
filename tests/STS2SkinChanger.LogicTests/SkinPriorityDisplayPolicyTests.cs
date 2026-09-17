using STS2SkinChanger.Core;

internal static class SkinPriorityDisplayPolicyTests
{
    private sealed record Option(string Id);

    public static void Run()
    {
        void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }

        var options = new[] { "skin:1", "skin:2", "skin:3", "skin:4" }
            .Select(id => new Option(id)).ToArray();
        string[] Order(IReadOnlyList<SkinPriorityState> entries) =>
            SkinPriorityDisplayPolicy.Project(options, option => option.Id, entries)
                .Select(option => option.Id).ToArray();

        // The saved list is deliberately not in name order, so any priority ordering is provable.
        var states = new[]
        {
            new SkinPriorityState("skin:4", true), new SkinPriorityState("skin:1", true),
            new SkinPriorityState("skin:3", false), new SkinPriorityState("skin:2", true)
        };
        Require(Order(states).SequenceEqual(["skin:4", "skin:1", "skin:2", "skin:3"]),
            "勾选的皮肤必须按手动顺序排在最前，未勾选的排在它们后面，当前为：" +
            string.Join(",", Order(states)) + "。");
        Require(Order(states).TakeWhile(id => id != "skin:3").SequenceEqual(["skin:4", "skin:1", "skin:2"]),
            "勾选的一半必须整体位于未勾选的一半之前。");

        // Un-ticking must move a skin below the ticked ones without disturbing the rest: here
        // skin:1 alone turns off, so it drops behind 4, 3 and 2 while those keep the saved order.
        var oneUnticked = new[]
        {
            new SkinPriorityState("skin:4", true), new SkinPriorityState("skin:1", false),
            new SkinPriorityState("skin:3", true), new SkinPriorityState("skin:2", true)
        };
        Require(Order(oneUnticked).SequenceEqual(["skin:4", "skin:3", "skin:2", "skin:1"]),
            "取消勾选只能把该皮肤移到已勾选项之后，其余顺序不变，当前为：" +
            string.Join(",", Order(oneUnticked)) + "。");
        var oneTicked = new[]
        {
            new SkinPriorityState("skin:4", false), new SkinPriorityState("skin:1", false),
            new SkinPriorityState("skin:3", true), new SkinPriorityState("skin:2", false)
        };
        Require(Order(oneTicked).SequenceEqual(["skin:3", "skin:4", "skin:1", "skin:2"]),
            "勾选后该皮肤必须升到最前，未勾选部分保持手动顺序，当前为：" +
            string.Join(",", Order(oneTicked)) + "。");

        // Both halves keep the saved order among themselves: that is the whole point of the split.
        string[] Unticked(IReadOnlyList<SkinPriorityState> entries) =>
            Order(entries).Where(id => entries.Any(entry => entry.OptionId == id && !entry.Enabled)).ToArray();
        Require(Unticked(states).SequenceEqual(["skin:3"]) &&
            Unticked(states.Select(entry => entry with { Enabled = false }).ToArray())
                .SequenceEqual(["skin:4", "skin:1", "skin:3", "skin:2"]),
            "全部未勾选时必须完整保留手动顺序，不能退化成原顺序以外的排列。");

        // Projection may reorder only: a caller's list must never gain, lose or duplicate a member.
        var shuffled = new[] { "skin:3", "skin:1" }.Select(id => new Option(id)).ToArray();
        Require(SkinPriorityDisplayPolicy.Project(shuffled, option => option.Id, states)
                .Select(option => option.Id).Order(StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(["skin:1", "skin:3"]),
            "排序只能改顺序：未记录的皮肤仍须保留在列表里。");
        Require(SkinPriorityDisplayPolicy.Project(options, option => option.Id,
                [new SkinPriorityState("skin:4", true)]).Select(option => option.Id)
                .SequenceEqual(["skin:4", "skin:1", "skin:2", "skin:3"]),
            "只有部分皮肤记录进优先级时，未记录项必须稳定落在尾部。");
        Require(SkinPriorityDisplayPolicy.Project(options, option => option.Id,
                [new SkinPriorityState("skin:9", true), new SkinPriorityState("", false)]).Select(option => option.Id)
                .SequenceEqual(["skin:1", "skin:2", "skin:3", "skin:4"]),
            "优先级里未知或空白的条目不能改变列表顺序。");
        Require(ReferenceEquals(SkinPriorityDisplayPolicy.Project(options, option => option.Id, []), options),
            "没有优先级记录时列表必须原样返回，供没有优先级界面的组直接复用。");
        Require(SkinPriorityDisplayPolicy.Project([], (Option option) => option.Id, states).Count == 0,
            "空列表投影后仍为空，不能因为优先级里有条目就凭空造出行。");

        // The dimming state must follow the same records, and never touch rows that are not skins.
        var lookup = SkinPriorityDisplayPolicy.UncheckedLookup(states);
        Require(lookup("SKIN:3") && !lookup("skin:4") && !lookup("skin:2"),
            "只有未勾选的皮肤需要降透明度，已勾选的必须保持原样。");
        Require(!lookup("__inherit__") && !lookup("__base__") && !lookup("__workshop__") &&
            !lookup("skin:5") && !lookup(""),
            "跟随分类、游戏原版、工坊入口和未记录的皮肤都不能被淡化或染色。");
        Require(!SkinPriorityDisplayPolicy.UncheckedLookup(null)("skin:3") &&
            !SkinPriorityDisplayPolicy.UncheckedLookup([])("skin:3"),
            "没有优先级界面时任何一行都不能被淡化。");
        var uncheckedAlpha = SkinPriorityDisplayPolicy.UncheckedAlpha;
        Require(uncheckedAlpha > .2f && uncheckedAlpha < 1f,
            "降透明度必须明显可辨且仍然可读，当前为 " + uncheckedAlpha + "。");
        Require(SkinPriorityDisplayPolicy.RowAlpha(excludedFromRandom: false, uncheckedForCategory: true) ==
                SkinPriorityDisplayPolicy.UncheckedAlpha &&
            SkinPriorityDisplayPolicy.RowAlpha(excludedFromRandom: true, uncheckedForCategory: true) ==
                SkinPriorityDisplayPolicy.UncheckedAlpha * .5f &&
            SkinPriorityDisplayPolicy.RowAlpha(excludedFromRandom: false, uncheckedForCategory: false) == 1f,
            "未勾选与随机排除的淡化必须叠加，不能互相覆盖。");

        Console.WriteLine("Skin priority display passed: ticked first in manual order, unticked stable below, unchecked rows dimmed.");
    }
}
