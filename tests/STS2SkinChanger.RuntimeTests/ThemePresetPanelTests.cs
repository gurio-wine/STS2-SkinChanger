using System.Reflection;
using Godot;
using HarmonyLib;

internal static class ThemePresetPanelTests
{
    internal static void Run(Assembly assembly)
    {
        var layout = assembly.GetType("STS2SkinChanger.Ui.ThemePresetPanelLayout");
        Require(layout != null, "主题预设需要独立的随动布局，不能仍塞在主面板的折叠分类内。");
        var place = AccessTools.Method(layout!, "Place");
        (Vector2, Rect2) Place(Vector2 viewport, Rect2 theme, Vector2 size) =>
            ((Vector2, Rect2))place.Invoke(null, [viewport, theme, size])!;
        var size = new Vector2(820, 480);
        var (main, presets) = Place(new(1920, 1080), new(36, 100, 540, 750), size);
        Require(main == new Vector2(36, 100) && presets == new Rect2(588, 370, 820, 480),
            "常规位置必须在主题右侧留 12 像素，并与主题底部对齐。");
        var (moved, following) = Place(new(1920, 1080), new(136, 180, 540, 750), size);
        Require(moved - main == new Vector2(100, 80) && following.Position - presets.Position == new Vector2(100, 80),
            "拖动主面板时，预设面板必须同步移动，不能固定在首次打开的位置。");
        foreach (var viewport in new[] { new Vector2(1920, 1080), new Vector2(1280, 720), new Vector2(900, 600) })
        {
            var height = Math.Min(750, viewport.Y - 24);
            var (position, panel) = Place(viewport, new(viewport.X - 100, viewport.Y - 100, 540, height), size);
            Require(position.X >= 12 && position.Y >= 12 && panel.End.X <= viewport.X - 12 && panel.End.Y <= viewport.Y - 12 &&
                    Math.Abs(panel.Position.X - position.X - 552) < .001f && Math.Abs(panel.End.Y - position.Y - height) < .001f,
                "靠近屏幕边缘或缩小窗口时，两块面板仍需右侧相邻、底部对齐且留在屏幕内。");
        }
        var (shortMain, tallPreset) = Place(new(1920, 1080), new(36, 12, 540, 300), size);
        Require(shortMain.Y == 192 && tallPreset.Position.Y == 12 && tallPreset.End.Y == shortMain.Y + 300,
            "预设比主面板高时，整体下移，不能让预设顶部出屏。");

        var editor = assembly.GetType("STS2SkinChanger.Ui.ModThemeEditor", true)!;
        Require(Calls(AccessTools.Method(editor, "ClampPanel"), editor, "PositionPresetPanel") &&
                Calls(AccessTools.Method(editor, "BuildPresetPanel"), typeof(CanvasItem), "add_ItemRectChanged"),
            "实际面板必须连接尺寸/位置变化，而不是只提供一个未使用的定位计算。");
        Require(Calls(AccessTools.Method(editor, "BuildPresetRows"), assembly.GetType("STS2SkinChanger.Ui.ScrollListRebuild", true)!, "Begin") &&
                Calls(AccessTools.Method(editor, "ReadValues"), editor, "RefreshPresetStates"),
            "逐项操作必须保留滚动位置，调参只更新已应用状态，不重建输入框和列表。");
        Console.WriteLine("Theme preset panel passed: right-bottom docking, dragging, viewport bounds and retained-list wiring.");
    }

    private static bool Calls(MethodBase method, Type type, string name) => PatchProcessor.GetOriginalInstructions(method)
        .Any(i => i.operand is MethodInfo m && m.DeclaringType == type && m.Name == name);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
