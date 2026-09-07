using System.Reflection;
using STS2SkinChanger;

internal static class WorkshopBrowserInteractionTests
{
    public static void Run()
    {
        WorkshopSubscriptionFilterTests.Run();
        CheckSessionCache();
        CheckTitleHoverTransitions();
        CheckLoadTagsAndSubmissionLinks();
        CheckNativeLinkControls();
        var assembly = typeof(Entry).Assembly;
        var policy = assembly.GetType("STS2SkinChanger.Core.WorkshopItemActions");
        Require(policy != null, "工坊需要按实际安装和订阅状态生成重启操作，不能只显示重启文字。");
        var key = assembly.GetType("STS2SkinChanger.Core.WorkshopTextKey")!;
        string? Action(bool subscribed, bool active, bool installed, bool known, string? state, bool busy = false) =>
            policy!.GetMethod("Primary")!.Invoke(null, [subscribed, active, installed, known, state == null ? null : Enum.Parse(key, state), busy])?.ToString();
        Require(Action(true, true, true, false, "Ready") == null, "已经加载的皮肤不能再显示可使用按钮。");
        Require(Action(true, false, true, false, "Restart") == "Restart", "安装完成且需要重启时应提供真正的重启入口。");
        Require(Action(true, false, true, true, null) == "Restart", "已安装但尚未加载的已审计皮肤也需重启入口。");
        Require(Action(false, true, true, false, "Ready") == "Subscribe", "本次保留资源不能阻止退订后的重新订阅。");
        Require(Action(false, false, false, true, null) == "Subscribe", "未订阅的皮肤不能用重启代替订阅。");
        Require(Action(true, false, false, true, "Restart") == "Download", "没有完整安装文件时不能提示点击重启即可使用。");
        Require(Action(true, false, true, false, "Restart", true) == "Download", "操作未结束时不能触发重启。");
        foreach (var state in new[] { "Ready", "Restart" })
            Require(policy!.GetMethod("Status")!.Invoke(null, [Enum.Parse(key, state)]) == null, "就绪和重启不能在状态栏重复显示。");
        Require(policy!.GetMethod("Status")!.Invoke(null, [Enum.Parse(key, "Failed")])?.ToString() == "Failed", "失败信息不能被去重逻辑隐藏。");
        Require((string)policy!.GetMethod("ItemUrl")!.Invoke(null, [123UL, false])! == "https://steamcommunity.com/sharedfiles/filedetails/?id=123" &&
            (string)policy.GetMethod("ItemUrl")!.Invoke(null, [123UL, true])! == "steam://url/CommunityFilePage/123", "Steam 页面入口只能由数字 ID 构造，不接收外部标题或封面链接。");
    }

    private static void CheckNativeLinkControls()
    {
        var assembly = typeof(Entry).Assembly;
        var panel = assembly.GetType("STS2SkinChanger.Ui.SkinWorkshopPanel", true)!;
        static bool Calls(MethodBase method, string target) => HarmonyLib.PatchProcessor.GetOriginalInstructions(method)
            .Any(i => i.operand is MethodInfo call && call.Name == target);
        var cover = HarmonyLib.AccessTools.Method(panel, "CreateCover");
        var title = HarmonyLib.AccessTools.Method(panel, "CreateMarquee");
        Require(Calls(cover, "add_Pressed") && Calls(title, "add_Pressed"), "封面和名字都必须绑定原生点击，不能只有鼠标光标。");
        foreach (var signal in new[] { "add_MouseEntered", "add_MouseExited", "add_VisibilityChanged", "add_TreeExiting" })
            Require(Calls(title, signal), "标题强调色需要由鼠标进入/离开和隐藏/退树事件控制，不能被遗留焦点锁住。");
        Require(assembly.GetType("STS2SkinChanger.Ui.WorkshopSubscriptionDialog") == null, "订阅后的独立重启弹窗已取消，不能保留另一路弹窗调度。");
        var links = assembly.GetType("STS2SkinChanger.Ui.WorkshopCommunityLinks", true)!;
        Require(Calls(HarmonyLib.AccessTools.Method(panel, "OpenItem"), "OpenItem") &&
            Calls(HarmonyLib.AccessTools.Method(links, "Open"), "ActivateGameOverlayToWebPage") &&
            Calls(HarmonyLib.AccessTools.Method(links, "Open"), "ShellOpen"), "物品和投稿必须共用 Steam 覆盖层/客户端回退路径。");
    }

    private static void CheckTitleHoverTransitions()
    {
        var type = typeof(Entry).Assembly.GetType("STS2SkinChanger.Ui.WorkshopTitleHover");
        Require(type != null, "标题必须直接处理进入/离开事件，不能在信号内反读引擎尚未更新的悬停状态。");
        var colors = new List<bool>();
        var hover = Activator.CreateInstance(type!, (Action<bool>)(accent => colors.Add(accent)))!;
        void Send(string name) => type!.GetMethod(name)!.Invoke(hover, null);
        Send("Refresh");
        Require(colors[^1] == false, "未悬停的初始标题应使用普通文字色。");
        Send("Enter");
        Require(colors[^1], "收到进入事件就应立即强调，不能等引擎更新状态。");
        Send("Refresh");
        Require(colors[^1], "悬停中调整主题应保留强调状态。");
        Send("Exit");
        Require(!colors[^1], "离开标题应立即恢复普通文字色，包括点击后鼠标离开的情况。");
        Send("Refresh");
        Require(!colors[^1], "离开后主题刷新不能再次染成强调色。");
        Send("Enter"); Send("Exit"); Send("Exit");
        Require(!colors[^1], "隐藏或关闭界面时重复清理不能反转颜色。");
    }

    private static void CheckLoadTagsAndSubmissionLinks()
    {
        var assembly = typeof(Entry).Assembly;
        var policy = assembly.GetType("STS2SkinChanger.Core.WorkshopLoadTags");
        Require(policy != null, "订阅前需要可筛选的重启标签，不能藏在下载按钮的悬停说明里。");
        var keyType = assembly.GetType("STS2SkinChanger.Core.WorkshopTextKey")!;
        var reasonType = assembly.GetType("STS2SkinChanger.Core.WorkshopLoadReason")!;
        string Tag(bool known, string? status, string reason = "None") => (string)policy!.GetMethod("Classify")!.Invoke(null,
            [known, status == null ? null : Enum.Parse(keyType, status), Enum.Parse(reasonType, reason)])!;
        Require(Tag(true, null) == "restart", "尚未订阅时也要显示内置清单已经确认的重启要求。");
        Require(Tag(false, null) == "hot", "清单已确认可免重启的包在订阅前就应显示正确标签。");
        Require(Tag(false, "Ready") == "hot" && Tag(true, "Ready") == "hot", "当前资源完整检查成功应覆盖旧清单。");
        Require(Tag(false, "Restart", "Dependency") == "restart", "下载后的实际检查结果应更新标签。");
        foreach (var reason in Enum.GetNames(reasonType))
            Require(Tag(true, "Failed", reason) == "restart" && Tag(false, "Failed", reason) == "hot",
                "下载/版本错误应独立显示具体原因，不能重新生成不兼容或待确认标签。");
        Require(((string[])policy!.GetField("FilterOptions")!.GetValue(null)!).SequenceEqual(new[] { "restart", "hot" }),
            "筛选只能提供需要重启和可免重启，不能残留不兼容或待确认入口。");
        bool Match(string filter, string tag) => (bool)policy!.GetMethod("Matches")!.Invoke(null, [filter, tag])!;
        Require(Match("", "hot") && Match("restart", "restart") && !Match("hot", "restart"), "标签筛选必须独立精确匹配，全部不限制结果。");
        var links = assembly.GetType("STS2SkinChanger.Core.WorkshopCommunityPolicy");
        Require(links != null, "两个投稿入口需要固定的讨论地址。");
        string Url(bool presets, bool client) => (string)links!.GetMethod("DiscussionUrl")!.Invoke(null, [presets, client])!;
        Require(Url(false, false) == "https://steamcommunity.com/workshop/filedetails/discussion/3787302680/592940620292752301", "投稿模组地址不匹配。");
        Require(Url(true, false) == "https://steamcommunity.com/workshop/filedetails/discussion/3787302680/592940620292746467", "投稿预设地址不匹配，不能串到模组投稿区。");
        Require(Url(true, true) == "steam://openurl/https://steamcommunity.com/workshop/filedetails/discussion/3787302680/592940620292746467", "无覆盖层时也应在 Steam 客户端打开，而不是操作系统网页浏览器。");
        var text = assembly.GetType("STS2SkinChanger.Core.WorkshopCommunityText", true)!;
        var textKey = assembly.GetType("STS2SkinChanger.Core.WorkshopCommunityTextKey", true)!;
        foreach (var language in new[] { "eng", "zhs", "zht", "deu", "esp", "spa", "fra", "ita", "jpn", "kor", "pol", "ptb", "rus", "tha", "tur" })
        foreach (var key in Enum.GetValues(textKey))
            Require(!string.IsNullOrWhiteSpace((string)text.GetMethod("ForLanguage")!.Invoke(null, [language, key])!), $"{language} 缺少投稿/重启标签文本 {key}");
        CheckExistingLoadedIsNotHot();
    }

    private static void CheckExistingLoadedIsNotHot()
    {
        var assembly = typeof(Entry).Assembly;
        var service = assembly.GetType("STS2SkinChanger.Core.SkinWorkshopService", true)!;
        var stateType = assembly.GetType("STS2SkinChanger.Core.WorkshopDownload", true)!;
        var keyType = assembly.GetType("STS2SkinChanger.Core.WorkshopTextKey", true)!;
        var policy = assembly.GetType("STS2SkinChanger.Core.WorkshopCatalogPolicy", true)!;
        var items = (Array)policy.GetMethod("Parse")!.Invoke(null, ["""[{"id":321,"restartRequired":true,"targets":[{"kind":"character","target":"silent"}]}]"""])!;
        var state = Activator.CreateInstance(stateType)!;
        stateType.GetField("State")!.SetValue(state, Enum.Parse(keyType, "Ready"));
        var downloads = (System.Collections.IDictionary)HarmonyLib.AccessTools.Field(service, "Downloads").GetValue(null)!;
        downloads.Add(321UL, state);
        try
        {
            string Tag() => (string)service.GetMethod("LoadTag")!.Invoke(null, [items.GetValue(0)])!;
            Require(Tag() == "restart", "启动时已加载不等于下载后可免重启，重新订阅不能把启动型 Mod 错标为免重启。");
            stateType.GetField("HotLoadVerified")!.SetValue(state, true);
            Require(Tag() == "hot", "实际免重启注册成功后才允许覆盖清单标签。");
        }
        finally { downloads.Remove(321UL); }
    }

    private static void CheckSessionCache()
    {
        var type = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.WorkshopSessionCache`1");
        Require(type != null, "缺少启动内共享缓存；翻页会重复请求资料或解码封面。");
        type = type!.MakeGenericType(typeof(string));
        var cache = Activator.CreateInstance(type)!;
        var get = type.GetMethod("Get")!;
        Task<string> Get(string id, Func<Task<string>> load, CancellationToken token = default) =>
            (Task<string>)get.Invoke(cache, [id, load, token])!;
        var calls = 0;
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<Task<string>> load = () => { calls++; return completion.Task; };
        using var cancelledPage = new CancellationTokenSource();
        var oldPage = Get("cover", load, cancelledPage.Token);
        var newPage = Get("cover", load);
        cancelledPage.Cancel();
        try { oldPage.GetAwaiter().GetResult(); throw new Exception("已关闭页面的等待没有取消。"); }
        catch (OperationCanceledException) { }
        completion.SetResult("thumbnail");
        Require(newPage.GetAwaiter().GetResult() == "thumbnail" && calls == 1, "快速翻页不能取消共享下载或启动第二次下载。");
        Require(Get("cover", () => { calls++; return Task.FromResult("wrong"); }).GetAwaiter().GetResult() == "thumbnail" && calls == 1,
            "重新打开已浏览页面必须直接复用缓存结果。");
        object?[] peek = ["cover", null];
        Require((bool)type.GetMethod("TryGet")!.Invoke(cache, peek)! && (string)peek[1]! == "thumbnail", "已加载缩略图应同步可取，避免重建时闪占位图。");
        Require(Get("other-language", () => Task.FromResult("localized")).Result == "localized", "不同语言/封面键不能串缓存。");
        var failures = 0;
        Func<Task<string>> fail = () => { failures++; return Task.FromException<string>(new IOException("offline")); };
        for (var i = 0; i < 2; i++)
        {
            try { Get("failed", fail).GetAwaiter().GetResult(); throw new Exception("未保留失败状态。"); }
            catch (IOException) { }
        }
        Require(failures == 1, "失败页面反复打开不能持续向外请求；下次启动再刷新。");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
