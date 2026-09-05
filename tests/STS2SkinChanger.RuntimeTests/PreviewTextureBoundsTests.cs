using Godot;
using HarmonyLib;
using STS2SkinChanger;

internal static class PreviewTextureBoundsTests
{
    internal static void Run()
    {
        var type = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.PreviewTextureBounds")
            ?? throw new InvalidOperationException("序列帧仍把透明画布算成人物大小。");
        var map = AccessTools.Method(type, "ToLocal");
        foreach (var (source, used, offset, centered, flipH, flipV, expected) in
                 new (Rect2, Rect2?, Vector2, bool, bool, bool, Rect2)[]
                 {
                     // Actual installed Jasmine idle frame: most of its 780x480 canvas is empty.
                     (new(0, 0, 780, 480), new(10, 309, 250, 163), new(220, -230), true, false, false,
                         new(-160, -161, 250, 163)),
                     (new(0, 0, 780, 480), new(10, 309, 250, 163), new(220, -230), true, true, false,
                         new(350, -161, 250, 163)),
                     (new(100, 50, 100, 80), new(120, 60, 30, 40), new(7, 9), false, false, true,
                         new(27, 39, 30, 40)),
                     // A region must not borrow the non-transparent content from another frame.
                     (new(100, 0, 100, 80), new(0, 0, 80, 80), Vector2.Zero, true, false, false, new()),
                     (new(0, 0, 100, 80), new(), Vector2.Zero, true, false, false, new()),
                     // Unreadable/dynamic textures retain their existing geometry.
                     (new(0, 0, 100, 80), null, new(7, 9), true, false, false, new(-43, -31, 100, 80))
                 })
        {
            var actual = (Rect2)map.Invoke(null, [source, used, offset, centered, flipH, flipV])!;
            if (!actual.IsEqualApprox(expected))
                throw new InvalidOperationException($"透明边界映射错误：{actual}，预期 {expected}。");
        }
        Console.WriteLine("Preview texture bounds passed: actual padded frame, offsets, flips, frame regions and safe fallback.");
    }
}
