using System.Reflection;
using STS2SkinChanger;

internal static class FrameworkSelectorCycleTests
{
    public static void Run()
    {
        var cycle = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.SkinOptionCycle")
            ?.GetMethod("NextOption", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("原管理器尚未使用 SC 可见选项的统一循环规则。");
        string? Next(string[] options, string? current, int direction) =>
            (string?)cycle.Invoke(null, [options, current, direction]);

        // A snapshot of the SC list, not the native registry: packs first, then vanilla,
        // ordinary providers and compositions. Hidden ingredients are absent by construction.
        string[] visible = ["bundle:常用", "vanilla", "foreign:one", "native:chen", "composition:one"];
        Require(Next(visible, "vanilla", 1) == "foreign:one", "下一项不能跳过外部皮肤。");
        Require(Next(visible, "native:chen", -1) == "foreign:one", "上一项也必须使用同一份 SC 顺序。");
        Require(Next(visible, "native:chen", 1) == "composition:one", "合并皮肤必须能被选中。");
        Require(Next(visible, "composition:one", 1) == "bundle:常用", "末尾必须循环到置顶的皮肤包。");
        Require(Next(visible, "bundle:常用", -1) == "composition:one", "第一项向前必须循环到末尾。");
        Require(Next(visible, "foreign:one", 1) == "native:chen", "连续切换应从待加载项继续，而不是返回原皮。");
        Require(Next(visible, "NATIVE:CHEN", 1) == "composition:one", "选项 ID 大小写不能导致循环位置丢失。");
        Require(Next(visible, "hidden:ingredient", 1) == "bundle:常用" &&
                Next(visible, "deleted:option", -1) == "composition:one", "隐藏或已删除的选择不能被重新插回列表。");
        Require(Next(["vanilla"], "vanilla", 1) == "vanilla", "只有原皮时不能越界。");
        Require(Next([], null, 1) == null, "空列表不能生成虚假皮肤请求。");
        Require(Next(visible, "foreign:one", 0) == "foreign:one", "没有方向时应保留当前选择。");
        Console.WriteLine("Framework selector cycle passed: shared SC order, packs, compositions, pending choices and wraparound.");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
