using System.Reflection;
using System.Text.Json;
using Godot;
using HarmonyLib;
using STS2SkinChanger;

internal static class WorkshopMetadataTests
{
    private static readonly Assembly Assembly = typeof(Entry).Assembly;
    private static Type Type(string name) => Assembly.GetType(name) ?? throw new InvalidOperationException("缺少生产实现：" + name);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    public static void Run()
    {
        WorkshopCarouselTests.Run();
        CheckSorting();
        CheckSteamFieldMapping();
        CheckIntroduction();
        CheckHover();
        CheckHoverDwell();
        CheckTextsAndRouting();
        Console.WriteLine("Workshop metadata, sorting and hover tests passed.");
    }

    private static void CheckSteamFieldMapping()
    {
        var reader = Type("STS2SkinChanger.Core.WorkshopMetadataReader");
        var item = new Steamworks.SteamUGCDetails_t { m_unVotesUp = 8, m_unVotesDown = 2,
            m_flScore = .7f, m_rtimeCreated = 100, m_rtimeUpdated = 200 };
        // Native marshalling allocates the SDK's fixed string buffers. Its managed
        // string setter alone does not initialize a default struct's null buffers.
        object boxed = item;
        foreach (var field in typeof(Steamworks.SteamUGCDetails_t).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            if (field.FieldType == typeof(byte[])) field.SetValue(boxed, new byte[field.GetCustomAttribute<System.Runtime.InteropServices.MarshalAsAttribute>()!.SizeConst]);
        typeof(Steamworks.SteamUGCDetails_t).GetProperty("m_rgchTitle")!.SetValue(boxed, "Test");
        item = (Steamworks.SteamUGCDetails_t)boxed;
        Func<Steamworks.EItemStatistic, ulong?> statistic = key => key switch
        {
            Steamworks.EItemStatistic.k_EItemStatistic_NumSubscriptions => 123,
            Steamworks.EItemStatistic.k_EItemStatistic_NumUniqueSubscriptions => 456,
            Steamworks.EItemStatistic.k_EItemStatistic_NumFavorites => 7,
            Steamworks.EItemStatistic.k_EItemStatistic_NumComments => null,
            _ => throw new InvalidOperationException("读取了与展示无关的 Steam 统计。")
        };
        var result = reader.GetMethod("Read")!.Invoke(null, [item, "cover", statistic])!;
        object? Field(string key) => result.GetType().GetProperty(key)!.GetValue(result);
        Require((ulong)Field("Subscriptions")! == 123 && (ulong)Field("LifetimeSubscriptions")! == 456 &&
            (ulong)Field("Favorites")! == 7 && Field("Comments") == null, "Steam 字段映射须使用各自的统计枚举，缺失的留言数据保留未知。");
        Require((float)Field("Score")! == .7f && (uint)Field("VotesUp")! == 8 && (uint)Field("Updated")! == 200,
            "不要用好评率覆盖 Steam 综合评分，日期保持独立。");
    }

    private static void CheckSorting()
    {
        var type = Type("STS2SkinChanger.Core.WorkshopSortPolicy");
        var detailType = Type("STS2SkinChanger.Core.WorkshopDetails");
        var kind = Type("STS2SkinChanger.Core.WorkshopSort");
        object Detail(string json) => JsonSerializer.Deserialize(json, detailType)!;
        var dictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(ulong), detailType);
        var details = (System.Collections.IDictionary)Activator.CreateInstance(dictionaryType)!;
        details.Add(1UL, Detail("""{"Title":"A","PreviewUrl":"","Subscriptions":10,"LifetimeSubscriptions":500,"Favorites":5,"Comments":7,"Score":0.8,"VotesUp":80,"VotesDown":20,"Created":100,"Updated":800}"""));
        details.Add(2UL, Detail("""{"Title":"B","PreviewUrl":"","Subscriptions":50,"LifetimeSubscriptions":100,"Favorites":9,"Comments":3,"Score":0.5,"VotesUp":1,"VotesDown":0,"Created":300,"Updated":200}"""));
        details.Add(3UL, Detail("""{"Title":"C","PreviewUrl":"","Subscriptions":0,"LifetimeSubscriptions":20,"Favorites":2,"Comments":11,"Score":0.7,"VotesUp":7,"VotesDown":3,"Created":200,"Updated":500}"""));
        details.Add(4UL, Detail("""{"Title":"old cache","PreviewUrl":""}"""));
        ulong[] Order(string sort, ulong[]? ids = null) => (ulong[])type.GetMethod("OrderIds")!.Invoke(null, [ids ?? new ulong[] { 4, 3, 2, 1, 5 }, details, Enum.Parse(kind, sort)])!;
        Require(Order("Subscriptions").SequenceEqual(new ulong[] { 2, 1, 3, 4, 5 }), "订阅最多须全量排序，已知零订阅应在未知统计之前，不能只排序当前页。");
        Require(Order("LifetimeSubscriptions").Take(3).SequenceEqual(new ulong[] { 1, 2, 3 }), "累计独立订阅不能复用当前订阅数字。");
        Require(Order("Rating").Take(3).SequenceEqual(new ulong[] { 1, 3, 2 }), "好评优先使用 Steam 综合评分，不把仅一票的 100% 好评排在前面。");
        Require(Order("Published").Take(3).SequenceEqual(new ulong[] { 2, 3, 1 }), "最新发布要用发布时间。");
        Require(Order("Updated").Take(3).SequenceEqual(new ulong[] { 1, 3, 2 }), "最近更新不能误用发布时间。");
        Require(Order("Comments").Take(3).SequenceEqual(new ulong[] { 3, 1, 2 }), "留言最多按留言数排序。");
        Require(Order("Favorites").Take(3).SequenceEqual(new ulong[] { 2, 1, 3 }), "收藏最多按收藏数排序。");
        Require(Order("Subscriptions", [5, 4]).SequenceEqual(new ulong[] { 4, 5 }), "统计同值/未知项需稳定次序，不能反复换位。");
        string Metric(string sort, object? detail) => (string)type.GetMethod("Metric")!.Invoke(null, [detail, Enum.Parse(kind, sort), "zhs"])!;
        Require(Metric("Subscriptions", details[1UL]).Contains("10") && Metric("LifetimeSubscriptions", details[1UL]).Contains("500"), "按钮前的数字必须来自当前排序字段。");
        Require(Metric("Comments", details[1UL]).Contains("7") && Metric("Favorites", details[1UL]).Contains("5"), "收藏/留言数字不能串用订阅数。");
        Require(Metric("Rating", details[1UL]).Contains("80") && Metric("Rating", details[1UL]).Contains("100"), "评分显示同时包含综合评分和评价总数。");
        Require(Metric("Subscriptions", details[4UL]) == "—" && Metric("Subscriptions", null) == "—", "旧缓存或查询失败不能谎报为零订阅。");
    }

    private static void CheckIntroduction()
    {
        var type = Type("STS2SkinChanger.Core.WorkshopIntroductionPolicy");
        string Summary(string input, int max = 200) => (string)type.GetMethod("Summary")!.Invoke(null, [input, max])!;
        var result = Summary("[h1]标题[/h1]\n[b]说明[/b] [url=https://example.com]详细介绍[/url]\n[img]https://example.com/a.png[/img][script]literal[/script]");
        Require(result.Contains("标题") && result.Contains("说明") && result.Contains("详细介绍") && !result.Contains("[b]") && !result.Contains("https://"), "简介应显示可读文字，不显示 BBCode 和图片地址，也不执行外部标记。");
        Require(Summary("a\r\n\r\nb").Contains("\n") && Summary("") == "", "摘要保留段落，空描述保持为空。");
        var shortText = Summary(string.Concat(Enumerable.Repeat("😀", 80)), 24);
        Require(shortText.EndsWith('…') && !shortText.Contains('\uFFFD') && !char.IsHighSurrogate(shortText[^2]), "摘要截断不能切开 emoji/Unicode 字符。");
        var images = (string[])type.GetMethod("Images")!.Invoke(null, ["https://a/cover", new[] { "https://a/one", "https://a/one", "", "https://a/two" }])!;
        Require(images.SequenceEqual(new[] { "https://a/one", "https://a/two" }), "轮播优先展示作者截图，去重；封面仅作为没有截图时的回退。");
        Require(((string[])type.GetMethod("Images")!.Invoke(null, ["https://a/cover", Array.Empty<string>()])!).SequenceEqual(new[] { "https://a/cover" }), "没有展示图时使用封面。");
    }

    private static void CheckHover()
    {
        var type = Type("STS2SkinChanger.Ui.WorkshopHoverPolicy");
        var viewport = new Rect2(0, 0, 1280, 720);
        foreach (var anchor in new[] { new Rect2(20, 10, 550, 160), new Rect2(690, 550, 570, 160) })
        {
            var rect = (Rect2)type.GetMethod("PlaceIntroduction")!.Invoke(null, [anchor, new Vector2(380, 400), viewport])!;
            Require(viewport.Encloses(rect), "简介不能越过屏幕边缘。");
            Require(!rect.Intersects(anchor), "空间足够时简介不能遮挡当前物品和订阅按钮。");
        }
        var display = type.GetMethod("CanDisplay");
        Require(display != null, "简介显示必须跟随当前悬停项，不能被简介自身延长。");
        bool CanDisplay(ulong hovered, ulong active, bool blocked) => (bool)display!.Invoke(null, [hovered, active, blocked])!;
        Require(CanDisplay(1, 1, false), "悬停当前物品时允许显示简介。");
        Require(!CanDisplay(0, 1, false) && !CanDisplay(2, 1, false), "离开或换项后立即停止旧简介，旧异步结果也不能覆盖新项。");
        Require(!CanDisplay(1, 1, true) && !CanDisplay(0, 0, false), "关闭、翻页或原生弹窗期间不能复活简介。");
    }

    private static void CheckHoverDwell()
    {
        var type = Type("STS2SkinChanger.Core.WorkshopHoverDwell");
        var dwell = Activator.CreateInstance(type)!;
        bool Ready(ulong id, double now) => (bool)type.GetMethod("Observe")!.Invoke(dwell, [id, now])!;
        void Reset() => type.GetMethod("Clear")!.Invoke(dwell, null);
        Require(!Ready(1, 10) && !Ready(1, 10.999), "简介及其资源请求不能在同一项悬停满一秒前开始。");
        Require(Ready(1, 11) && Ready(1, 11.1), "连续悬停满一秒才允许显示，同一项不能重复重置计时。");
        Require(!Ready(2, 11.2) && !Ready(2, 12), "从已打开简介的物品移到另一个物品也须重新等待一秒。");
        Require(!Ready(1, 12.1) && !Ready(1, 13), "快速扫过或返回旧项不能累积之前的悬停时间。");
        Require(Ready(1, 13.1), "重入后连续停留一秒应正常显示。");
        Require(!Ready(0, 14) && !Ready(1, 15) && !Ready(1, 15.9), "离开物品须清除计时，即使其简介已经缓存。");
        Reset();
        Require(!Ready(1, 20) && Ready(1, 21), "滚动、翻页或弹出其它窗口后必须重新等待。");
    }

    private static void CheckTextsAndRouting()
    {
        var type = Type("STS2SkinChanger.Core.WorkshopDetailsText");
        var keyType = Type("STS2SkinChanger.Core.WorkshopDetailsTextKey");
        foreach (var language in new[] { "eng", "zhs", "zht", "deu", "esp", "spa", "fra", "ita", "jpn", "kor", "pol", "ptb", "rus", "tha", "tur" })
        foreach (var key in Enum.GetValues(keyType))
            Require(!string.IsNullOrWhiteSpace((string)type.GetMethod("ForLanguage")!.Invoke(null, [language, key])!), $"{language} 缺少工坊排序/简介文本 {key}");
        var panel = Type("STS2SkinChanger.Ui.SkinWorkshopPanel");
        static bool Calls(MethodBase method, string target) => PatchProcessor.GetOriginalInstructions(method).Any(i => i.operand is MethodInfo call && call.Name == target);
        Require(!Calls(AccessTools.Method(panel, "CreateCover"), "add_Pressed") && !Calls(AccessTools.Method(panel, "CreateMarquee"), "add_Pressed"), "名字和封面不再单独绑定跳转，统一由整个物品项响应点击。");
        Require(!Calls(AccessTools.Method(panel, "CreateIntroduction"), "add_Pressed") &&
            AccessTools.Method(panel, "AttachItemClick") is { } click && Calls(click, "add_GuiInput"),
            "点击入口属于物品项，不是简介；简介不能拦截鼠标或绑定点击。");
        Require(!PatchProcessor.GetOriginalInstructions(AccessTools.Method(panel, "CreateIntroduction")).Any(i =>
            i.operand is ConstructorInfo constructor && typeof(Button).IsAssignableFrom(constructor.DeclaringType!)),
            "简介中不能留下可拦截鼠标的轮播按钮。");
        Require(Calls(AccessTools.Method(panel, "HandleInput"), "UpdateHover") &&
            Calls(AccessTools.Method(panel, "UpdateHover"), "CancelIntroduction") &&
            Calls(AccessTools.Method(panel, "IntroductionCurrent"), "CanDisplay"),
            "鼠标移动离项须立即隐藏简介，异步详情/图片返回前须再次核对悬停项。");
        Require(Calls(AccessTools.Method(panel, "InitializeHover"), "add_ProcessFrame") &&
            Calls(AccessTools.Method(panel, "Cleanup"), "remove_ProcessFrame"),
            "悬停应每帧更新，关闭面板须解除帧事件，不能依赖低频走马灯计时器。");
        Require(!Calls(AccessTools.Method(panel, "Lift"), "set_ShadowSize") && !Calls(AccessTools.Method(panel, "Lift"), "set_ShadowOffset"),
            "抬起物品不能在主题面板外额外套厚重阴影边缘。");
        Require(!Calls(AccessTools.Method(panel, "Lift"), "CreateTween") && Calls(AccessTools.Method(panel, "Lift"), "set_Scale"),
            "移入物品必须立即放大，不能等待放大动画。");
        Require(AccessTools.Method(panel, "BeginReturn") is { } shrink && Calls(shrink, "CreateTween") && !Calls(shrink, "Reparent"),
            "移出时缩小过渡应保留在浮动层，不能提前退回滚动裁剪区域。");
        Require(!Calls(AccessTools.Method(panel, "Lift"), "Reparent") && !Calls(AccessTools.Method(panel, "RestoreItem"), "Reparent"),
            "悬停不能通过重新挂载整棵物品节点抬升；这会同步重跑主题绑定、文字布局和背景初始化。");
        Require(Calls(AccessTools.Method(panel, "Lift"), "set_TopLevel") && Calls(AccessTools.Method(panel, "RestoreItem"), "set_TopLevel"),
            "物品需通过原生画布顶层模式绕过滚动裁剪，退场后恢复原布局，而非只修改 ZIndex。");
        Require(!Calls(AccessTools.Method(panel, "Lift"), "set_Text") &&
            !Calls(AccessTools.Method(panel, "Lift"), "LoadIntroduction") &&
            Calls(AccessTools.Method(panel, "UpdateHover"), "Observe"),
            "切项路径只抬升物品；文字布局和简介资源请求必须受一秒悬停计时控制。");
    }
}
