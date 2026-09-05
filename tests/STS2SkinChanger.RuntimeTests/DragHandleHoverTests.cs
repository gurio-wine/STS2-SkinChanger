using System.Reflection;
using Godot;
using HarmonyLib;
using STS2SkinChanger;

internal static class DragHandleHoverTests
{
    internal static void Run()
    {
        var assembly = typeof(Entry).Assembly;
        var hover = assembly.GetType("STS2SkinChanger.Ui.DragHandleHoverVisibility")
            ?? throw new InvalidOperationException("拖拽柄尚未统一为悬停显示。");
        CheckExplicitLifecycle(hover);
        var reveal = AccessTools.Method(hover, "ShouldReveal");
        foreach (var (visible, focused, dragging, pressed, over, expected) in new[]
        {
            (true, true, false, false, false, false), // initially away
            (true, true, false, false, true, true),   // button, child or grip
            (true, true, true, true, false, true),    // captured drag outside the bounds
            (true, true, true, false, false, false),  // release outside, even with a stale flag
            (true, true, false, false, false, false), // leave the whole group
            (false, true, true, true, true, false),   // closed/hidden screen
            (true, false, true, true, true, false)    // lost window focus
        })
            Require((bool)reveal.Invoke(null, [visible, focused, dragging, pressed, over])! == expected,
                "拖拽柄应覆盖按钮及自身悬停、持续拖动、松手离开、隐藏及失焦。");

        var drag = assembly.GetType("STS2SkinChanger.Ui.DraggableSkinControl", true)!;
        var cards = assembly.GetType("STS2SkinChanger.Ui.CardInspectSkinControls", true)!;
        Require(Calls(AccessTools.Method(drag, "Initialize"), hover, "Attach") &&
                Calls(AccessTools.Method(cards, "Attach"), hover, "Attach"),
            "选角/合并/皮肤包/预览与单卡选择器必须共用相同悬停规则。");
        var apply = AccessTools.Method(hover, "ApplyShown");
        Require(Calls(apply, typeof(CanvasItem), "set_SelfModulate") &&
                !Calls(apply, typeof(CanvasItem), "set_Visible"),
            "隐藏拖拽柄只能改变绘制透明度，不能导致容器重排或失去鼠标命中区。");
        Console.WriteLine("Drag handle hover passed: shared selectors, pointer/drag lifetime and stable layout.");
    }

    private static void CheckExplicitLifecycle(Type hover)
    {
        var attach = AccessTools.Method(hover, "Attach");
        Require(Calls(attach, hover, "Initialize"),
            "Attach 必须显式初始化悬停事件，不能隐藏后等待未触发的 _Ready。");
        Require(Calls(attach, typeof(Node), "add_TreeEntered") &&
                Calls(attach, typeof(Node), "add_TreeExiting"),
            "预览尚未进树及缓存界面重进时，必须通过原生生命周期信号连接和释放。");
        var watch = AccessTools.Method(hover, "WatchHoverTarget");
        Require(watch != null && Calls(watch, typeof(Control), "add_MouseEntered") &&
                Calls(watch, typeof(Control), "add_MouseExited") && Calls(watch, typeof(Control), "add_GuiInput"),
            "整个按钮组及拖拽柄必须直接接收原生鼠标信号，不能依赖 _Input 自动回调。");
        var disconnect = AccessTools.Method(hover, "Disconnect");
        Require(disconnect != null && Calls(disconnect, typeof(Control), "remove_MouseEntered") &&
                Calls(disconnect, typeof(Control), "remove_MouseExited") && Calls(disconnect, typeof(Control), "remove_GuiInput"),
            "离开界面后需要释放输入事件，避免重进重复订阅或访问已释放节点。");
    }

    private static bool Calls(MethodInfo method, Type type, string name) =>
        PatchProcessor.GetOriginalInstructions(method).Any(i => i.operand is MethodInfo called &&
            called.DeclaringType == type && called.Name == name);

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
