using STS2SkinChanger;

internal static class WorkshopCarouselTests
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    public static void Run()
    {
        var definition = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.WorkshopCarousel`1");
        Require(definition != null, "轮播应提前加载下一张，不能在显示时才发起下载。");
        var type = definition!.MakeGenericType(typeof(string));
        var requests = new List<string>();
        var pending = new Dictionary<string, TaskCompletionSource<string?>>();
        var shown = new List<(string? Image, int Index)>();
        double now = 0;
        Func<string, CancellationToken, Task<string?>> load = (url, _) =>
        {
            requests.Add(url);
            if (!pending.TryGetValue(url, out var task)) pending[url] = task = new();
            return task.Task;
        };
        Action<string?, int, int> show = (image, index, _) => shown.Add((image, index));
        var carousel = Activator.CreateInstance(type, load, show, (Func<double>)(() => now))!;
        void Start(params string[] urls) => type.GetMethod("Start")!.Invoke(carousel, [urls, CancellationToken.None]);
        void Tick(bool autoAdvance = true) => type.GetMethod("Tick")!.Invoke(carousel, [autoAdvance]);
        void Clear() => type.GetMethod("Clear")!.Invoke(carousel, null);

        Start("a", "b", "c");
        Require(requests.SequenceEqual(new[] { "a" }), "先加载首图，不应在首次悬停就下载全部展示图。");
        pending["a"].SetResult("image-a"); Tick();
        Require(shown.SequenceEqual(new[] { ((string?)"image-a", 0) }) && requests.SequenceEqual(new[] { "a", "b" }),
            "首图就绪应立即显示并预加载第二张，不等待轮播时间。");
        now = 1.9; pending["b"].SetResult("image-b"); Tick();
        Require(shown.Count == 1 && requests.Count == 2, "预加载完成不应提前切图或继续下载整套图片。");
        now = 2; Tick();
        Require(shown[^1] == ("image-b", 1) && requests.SequenceEqual(new[] { "a", "b", "c" }),
            "两秒轮播应直接显示缓存的下一张，并只提前准备再下一张。");
        now = 4; Tick();
        Require(shown.Count == 2, "慢网速下保留当前图片，不能切空白或阻塞主线程等待。");
        pending["c"].SetResult(null); Tick();
        Require(shown.Count == 2, "下一张下载失败不能清除已经显示的图片。");
        now = 6; Tick();
        Require(shown[^1] == ("image-a", 0), "坏图之后应能继续轮播并循环回第一张。");

        Clear(); Start("old", "old-next"); Clear(); Start("new", "new-next");
        pending["old"].SetResult("stale"); Tick();
        Require(shown.All(item => item.Image != "stale") && !requests.Contains("old-next"), "换项后旧下载不能显示或继续预取。");
        pending["new"].SetResult("new-image"); Tick(); Clear();
        var stoppedCount = shown.Count; var stoppedRequests = requests.Count;
        pending["new-next"].SetResult("late"); now = 100; Tick();
        Require(shown.Count == stoppedCount && requests.Count == stoppedRequests, "关闭面板后不能继续切图或发起下一轮预取。");

        Start("a"); Tick(); now = 102; Tick();
        Require(shown[^1] == ("image-a", 0) && requests.Count == stoppedRequests + 1, "单张图片不应反复加载。");
        Clear(); Start("a", "b"); Tick(false); var instantCount = shown.Count;
        now = 110; Tick(false);
        Require(shown.Count == instantCount, "即时动画模式显示首图，但不强制自动轮播。");
        Clear(); Start(); Tick(); Clear();
        Console.WriteLine("Workshop carousel passed: two-second cadence, one-ahead preload, slow/failing images and cancellation.");
    }
}
