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
        CheckEngineCallbacks(hover);
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
        Require(Calls(AccessTools.Method(hover, "_ExitTree"), typeof(Node), "RequestReady"),
            "缓存界面重新进入场景树时，必须重新接入悬停与窗口事件。");
        Console.WriteLine("Drag handle hover passed: shared selectors, pointer/drag lifetime and stable layout.");
    }

    private static void CheckEngineCallbacks(Type hover)
    {
        // This DLL uses Microsoft.NET.Sdk, not Godot's script generators. An ordinary C#
        // override alone does not advertise the callback to Godot's native method lookup.
        var lookup = AccessTools.Method(hover, "HasGodotClassMethod");
        Require(lookup.DeclaringType == hover,
            "悬停组件只有 C# override、缺少 Godot 回调注册：隐藏后 _Ready/_Input 不会运行。");
        var lookupCode = PatchProcessor.GetOriginalInstructions(lookup);
        var dispatch = AccessTools.Method(typeof(Node), "InvokeGodotClassMethod");
        foreach (var name in new[] { "_Ready", "_Input", "_ExitTree" })
        {
            Require(lookupCode.Any(i => i.operand is FieldInfo field &&
                        field.DeclaringType == typeof(Node.MethodName) && field.Name == name),
                "必须向引擎注册原生回调名，不能只声明托管方法：" + name);
            Require(Calls(dispatch, typeof(Node), name), "当前游戏的 Godot 基类必须能分派已注册回调：" + name);
        }
        Require(Calls(lookup, typeof(Node), "HasGodotClassMethod"), "其它引擎方法的识别必须保留基类回退。");
    }

    private static bool Calls(MethodInfo method, Type type, string name) =>
        PatchProcessor.GetOriginalInstructions(method).Any(i => i.operand is MethodInfo called &&
            called.DeclaringType == type && called.Name == name);

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
