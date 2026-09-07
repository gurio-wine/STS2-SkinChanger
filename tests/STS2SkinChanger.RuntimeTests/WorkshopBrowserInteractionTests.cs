using System.Reflection;
using STS2SkinChanger;

internal static class WorkshopBrowserInteractionTests
{
    public static void Run()
    {
        CheckSessionCache();
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
