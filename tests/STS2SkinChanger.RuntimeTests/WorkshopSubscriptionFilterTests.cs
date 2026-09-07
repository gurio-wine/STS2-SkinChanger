using System.Reflection;
using STS2SkinChanger;

internal static class WorkshopSubscriptionFilterTests
{
    public static void Run()
    {
        var type = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.WorkshopSubscriptionFilter");
        Require(type != null, "工坊缺少默认未订阅的独立筛选。");
        var filter = Activator.CreateInstance(type!)!;
        object? Call(string name, params object?[] args) => type!.GetMethod(name)!.Invoke(filter, args);
        bool Matches(ulong id) => (bool)Call("Matches", id)!;
        var steam = new Dictionary<ulong, bool> { [1] = true, [2] = false, [3] = false };
        void Refresh(Func<ulong, bool>? read = null) => Call("Refresh", new ulong[] { 1, 2, 3 }, read ?? (id => steam[id]));

        Require(!Matches(1) && !Matches(2), "尚未取得订阅状态不能擅自归为未订阅。");
        Refresh();
        Require(!Matches(1) && Matches(2) && Matches(3), "初次打开必须默认筛出未订阅，不能按已安装或已加载判断。");
        Call("Select", "");
        Require(Matches(1) && Matches(2) && Matches(999), "全部不应被订阅状态或离线未知状态过滤。");
        Call("Select", "subscribed");
        Require(Matches(1) && !Matches(2), "已订阅只能显示 Steam 订阅记录里的物品。");
        Call("Select", "unsubscribed");
        Call("BeginAction", 2UL);
        steam[2] = true; Refresh();
        Require(Matches(2), "刚订阅后仍在下载的行必须保留进度，不能立刻被筛掉。");
        Call("EndAction", 2UL);
        Require(!Matches(2), "下载结束后必须按真实订阅状态归类。");
        steam[1] = false; Refresh();
        Require(Matches(1), "在 Steam 中取消订阅后，即使文件尚存也应进入未订阅。");
        Call("BeginAction", 2UL);
        Call("Select", "subscribed");
        steam[2] = false; Refresh();
        Require(!Matches(2), "玩家主动换筛选后，旧操作不能把不匹配行带进新筛选。");

        Refresh(_ => throw new InvalidOperationException("Steam unavailable"));
        Require(!Matches(2) && (bool)type!.GetProperty("Unavailable")!.GetValue(filter)!, "临时读取失败须保留最近的订阅记录，且标记不可用。");
        var offline = Activator.CreateInstance(type!)!;
        type!.GetMethod("Refresh")!.Invoke(offline, [new ulong[] { 1 }, (Func<ulong, bool>)(_ => throw new InvalidOperationException())]);
        Require(!(bool)type.GetMethod("Matches")!.Invoke(offline, [1UL])!, "离线且从未读取过的物品不能被误标未订阅。");
        Refresh();
        Require(!(bool)type.GetProperty("Unavailable")!.GetValue(filter)!, "Steam 恢复后须清除不可用状态。");

        const string json = """[{"id":1,"targets":[{"kind":"cards","target":"silent"}]},{"id":2,"targets":[{"kind":"cards","target":"silent"}]},{"id":3,"targets":[{"kind":"monster","target":"jaw_worm"}]}]""";
        var catalogPolicy = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.WorkshopCatalogPolicy", true)!;
        var browserPolicy = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.WorkshopBrowserPolicy", true)!;
        var items = catalogPolicy.GetMethod("Parse")!.Invoke(null, [json]);
        var cards = browserPolicy.GetMethod("Filter")!.Invoke(null, [items, "cards", "silent", null]);
        steam[2] = true; Refresh(); Call("Select", "unsubscribed");
        var filteredCards = ((System.Collections.IEnumerable)Call("Filter", cards)!).Cast<object>()
            .Select(item => (ulong)item.GetType().GetProperty("Id")!.GetValue(item)!).ToArray();
        Require(filteredCards.SequenceEqual(new ulong[] { 1 }), "订阅筛选须和类型/对象筛选取交集，不能放出其它对象或已订阅项。");

        var panel = typeof(Entry).Assembly.GetType("STS2SkinChanger.Ui.SkinWorkshopPanel", true)!;
        bool Calls(string method, string declaringType, string target) => HarmonyLib.PatchProcessor.GetOriginalInstructions(HarmonyLib.AccessTools.Method(panel, method))
            .Any(i => i.operand is MethodInfo called && called.DeclaringType?.Name == declaringType && called.Name == target);
        Require(Calls("Rebuild", "WorkshopSubscriptionFilter", "Refresh") && Calls("FilteredItems", "WorkshopSubscriptionFilter", "Filter"),
            "真实列表重建必须读取 Steam 记录并应用订阅筛选，不能只做好独立策略。");
        Require(Calls("Poll", "SkinWorkshopPanel", "PollFilters") && Calls("PollFilters", "WorkshopSubscriptionFilter", "Refresh"),
            "打开面板期间也要更新订阅记录，包括不在当前页上的物品。");
        foreach (var name in new[] { "Subscribe", "Unsubscribe" })
        {
            var method = HarmonyLib.AccessTools.Method(panel, name);
            var machine = method.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>()!.StateMachineType;
            var operations = HarmonyLib.PatchProcessor.GetOriginalInstructions(HarmonyLib.AccessTools.Method(machine, "MoveNext"))
                .Select(i => i.operand).OfType<MethodInfo>().Where(m => m.DeclaringType == type).Select(m => m.Name).ToArray();
            Require(operations.Contains("BeginAction") && operations.Contains("EndAction"), "真实订阅/退订流程必须开启并释放下载行的临时保留。");
        }
        Console.WriteLine("Workshop subscription filters passed: defaults, live membership, pending downloads and offline recovery.");
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
