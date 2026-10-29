using STS2SkinChanger.Core;
using STS2SkinChanger.Ui;

internal static class WorkshopOptionSelectionPolicyTests
{
    public static void Run()
    {
        void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }

        // GitHub 的排序下拉按「星标 / 最近更新 / 名称」的顺序添加条目，而枚举从「最近更新」开始：
        // 按索引选择会把星标一项选成第二个条目，玩家看到的就是「最近更新」。
        int[] githubItems = [(int)GitHubSort.Stars, (int)GitHubSort.Updated, (int)GitHubSort.Name];
        Require(WorkshopOptionSelectionPolicy.IndexOfId(githubItems, (int)GitHubSort.Stars) == 0 &&
            WorkshopOptionSelectionPolicy.IndexOfId(githubItems, (int)GitHubSort.Updated) == 1 &&
            WorkshopOptionSelectionPolicy.IndexOfId(githubItems, (int)GitHubSort.Name) == 2,
            "排序下拉必须按条目 ID 选择：按索引选会让星标显示成最近更新。");
        Require(WorkshopOptionSelectionPolicy.IndexOfId([(int)GitHubSort.Updated, (int)GitHubSort.Stars], (int)GitHubSort.Stars) == 1,
            "索引与 ID 偶然一致时也要按 ID 找到正确的条目。");
        Require(WorkshopOptionSelectionPolicy.IndexOfId([0, 1], 7) == -1 &&
            WorkshopOptionSelectionPolicy.IndexOfId([], 0) == -1,
            "找不到对应 ID 时不能返回 0，否则会把玩家的排序换成另一个选项。");
        Console.WriteLine("Workshop option selection passed: pickers select by item id, a missing id stays untouched.");
    }
}
