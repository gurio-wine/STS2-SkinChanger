using Godot;
using HarmonyLib;
using STS2SkinChanger;

internal static class RenderedPreviewFramingTests
{
    internal static void Run()
    {
        var type = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.RenderedPreviewFraming")
            ?? throw new InvalidOperationException("预览仍未根据实际渲染内容取景。");
        object Create(Rect2 bounds) => Activator.CreateInstance(type, bounds, new Vector2I(200, 300))!;
        Transform2D Camera(object state) => (Transform2D)AccessTools.Property(type, "CanvasTransform").GetValue(state)!;
        bool Complete(object state) => (bool)AccessTools.Property(type, "Complete").GetValue(state)!;
        bool Observe(object state, Rect2? pixels) => (bool)AccessTools.Method(type, "Observe").Invoke(state, [pixels])!;

        // All implementations have the same observable input: pixels actually drawn. Geometry
        // can be padded, off-centre or authored at a different scale without changing framing.
        foreach (var (seed, visible) in new (Rect2, Rect2)[]
        {
            (new(-1000, -2000, 2000, 3000), new(-50, -200, 100, 200)),
            (new(200, 200, 9000, 15000), new(300, 500, 100, 200)),
            (new(-5000, -8000, 10000, 10000), new(-200, -400, 200, 400))
        })
        {
            var state = Create(seed);
            for (var frame = 0; frame < 6 && !Complete(state); frame++)
                Observe(state, Camera(state) * visible);
            var actual = Camera(state) * visible;
            Require(Complete(state) && Near(actual, new Rect2(29, 8, 142, 284)),
                $"透明留白或原始比例仍影响最终人物取景：{actual}。");
            var frozen = Camera(state);
            Require(!Observe(state, new Rect2(0, 0, 200, 300)) && Camera(state) == frozen,
                "测量完成后不能持续根据动作变更缩放。");
        }

        var empty = Create(new Rect2(-100, -300, 200, 300));
        var original = Camera(empty);
        Require(Observe(empty, null) && Camera(empty) == original,
            "未就绪空帧不能让相机归零或无限放大。");
        var visibleAfterDelay = new Rect2(-50, -200, 100, 200);
        Observe(empty, Camera(empty) * visibleAfterDelay);
        Observe(empty, Camera(empty) * visibleAfterDelay);
        Require(Complete(empty) && Near(Camera(empty) * visibleAfterDelay, new Rect2(29, 8, 142, 284)),
            "空帧后就绪的模型仍必须正常校准。");

        var clipped = Create(new Rect2(-100, -300, 200, 300));
        var oldScale = Camera(clipped).X.X;
        Require(Observe(clipped, new Rect2(0, 20, 80, 200)) && Camera(clipped).X.X < oldScale,
            "触碰画布边缘说明可能被裁切，应先拉远，不能把残缺内容放大。");

        var failed = Create(new Rect2(-100, -300, 200, 300));
        var reads = 0;
        while (!Complete(failed) && reads < 30) { Observe(failed, null); reads++; }
        Require(Complete(failed) && reads <= 6 && Camera(failed).IsFinite(),
            "持续空画面必须有限结束，不得永久逐帧回读。");

        var cancelled = Create(new Rect2(-100, -300, 200, 300));
        var cancelledCamera = Camera(cancelled);
        AccessTools.Method(type, "Cancel").Invoke(cancelled, null);
        Require(!Observe(cancelled, new Rect2(30, 30, 60, 200)) && Camera(cancelled) == cancelledCamera,
            "快速切走后，迟到的渲染结果不能再修改旧预览。");
        var untouched = Create(new Rect2(-100, -300, 200, 300));
        Observe(clipped, null);
        Require(Camera(untouched) == original && !Complete(untouched),
            "不同皮肤的相机和测量状态不能共享。");
        Console.WriteLine("Rendered preview framing passed: content-driven fit, clipping, empty frames, finite reads, cancellation and isolation.");
    }

    private static bool Near(Rect2 actual, Rect2 expected) =>
        actual.Position.DistanceTo(expected.Position) < .01f && actual.Size.DistanceTo(expected.Size) < .01f;

    private static void Require(bool value, string error)
    {
        if (!value) throw new InvalidOperationException(error);
    }
}
