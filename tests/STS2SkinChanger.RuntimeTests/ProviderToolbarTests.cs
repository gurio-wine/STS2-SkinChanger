using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using STS2SkinChanger;

internal static class ProviderToolbarTests
{
    private static Control _toolbar = null!;
    private static Control _strip = null!;
    private static bool _stripClips;
    private static bool _promoted;

    public static void Run()
    {
        CheckStripClipping();
        CheckPlacement();
        CheckFade();
        Console.WriteLine("Provider toolbar passed: character strip clipping, authored layout and repeated promotion.");
    }

    private static void CheckStripClipping()
    {
        // Execute the actual stabilizer. Only native engine access is substituted;
        // turning off the strip's clip flag (the original bug) must fail this test.
        _toolbar = (Control)RuntimeHelpers.GetUninitializedObject(typeof(Control));
        _strip = (Control)RuntimeHelpers.GetUninitializedObject(typeof(Control));
        _stripClips = true;
        _promoted = false;
        var harmony = new Harmony("tests.provider-toolbar.engine");
        try
        {
            foreach (var method in new[] { AccessTools.PropertySetter(typeof(CanvasItem), "Visible"),
                         AccessTools.PropertySetter(typeof(CanvasItem), "ZAsRelative"),
                         AccessTools.PropertySetter(typeof(CanvasItem), "ZIndex"),
                         AccessTools.Method(typeof(CanvasItem), "MoveToFront") })
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(ProviderToolbarTests), nameof(SkipNative)));
            Patch(harmony, typeof(CanvasItem), "get_ZIndex", nameof(GetZ));
            Patch(harmony, typeof(Control), "set_ClipContents", nameof(SetClip));
            Patch(harmony, typeof(Control), "get_ClipContents", nameof(GetClip));
            Patch(harmony, typeof(Node), "GetParent", nameof(GetParent));
            Patch(harmony, typeof(Control), "get_GlobalPosition", nameof(GetPosition));
            Patch(harmony, typeof(Control), "get_Size", nameof(GetSize));
            // Binding owns a native processing node; its geometry policy is exercised below.
            if (typeof(Entry).Assembly.GetType("STS2SkinChanger.Ui.ProviderToolbarCanvas") is { } bridge)
                harmony.Patch(AccessTools.Method(bridge, "Bind"), prefix:
                    new HarmonyMethod(typeof(ProviderToolbarTests), nameof(Promote)));
            var runtime = typeof(Entry).Assembly.GetType("STS2SkinChanger.Ui.ContextualSkinControls", true)!;
            try
            {
                AccessTools.Method(runtime, "StabilizeProviderCharacterSelectControl").Invoke(null,
                    [RuntimeHelpers.GetUninitializedObject(typeof(NCharacterSelectScreen)), _toolbar]);
            }
            catch (TargetInvocationException) when (!_stripClips) { /* Fail at the native clipping boundary. */ }
            Require(_stripClips, "CZN 设置面板不得关闭角色按钮区域裁剪，否则翻页外的角色会露出。");
            Require(_promoted, "设置面板仍须能越过裁剪边界，不能连同设置按钮一起裁掉。");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }

    private static void CheckPlacement()
    {
        var type = typeof(Entry).Assembly.GetType("STS2SkinChanger.Ui.ToolbarCanvasPlacement", true)!;
        var state = Activator.CreateInstance(type, nonPublic: true)!;
        var resolve = AccessTools.Method(type, "Resolve");
        (Vector2, Vector2, float) Apply(Vector2 position, Vector2 scale, float rotation, Transform2D parent) =>
            ((Vector2, Vector2, float))resolve.Invoke(state, [position, scale, rotation, Vector2.Zero, parent])!;
        var parent = new Transform2D(0, new Vector2(496, 880));
        var first = Apply(new(-368, 26), Vector2.One, 0, parent);
        Require(first.Item1.IsEqualApprox(new(128, 906)), "脱离裁剪不能改变作者面板的屏幕位置。");
        Require(Apply(first.Item1, first.Item2, 0, parent).Item1.IsEqualApprox(new(128, 906)),
            "延迟或重复整理面板不能重复叠加父级偏移。");
        var moved = Apply(first.Item1, first.Item2, 0, new(0, new Vector2(520, 900)));
        Require(moved.Item1.IsEqualApprox(new(152, 926)), "父级布局移动后，面板必须保留原来的相对位置。");
        var authored = Apply(new(-400, 30), Vector2.One, 0, new(0, new Vector2(520, 900)));
        Require(authored.Item1.IsEqualApprox(new(120, 930)), "作者再次布局时应采用新坐标，而不是锁死旧位置。");
        var scaled = Apply(authored.Item1, authored.Item2, 0, new(0, new Vector2(2, 2), 0, new(520, 900)));
        Require(scaled.Item1.IsEqualApprox(new(-280, 960)) && scaled.Item2.IsEqualApprox(new(2, 2)),
            "UI 缩放后仍须继承原父级的缩放。");

        var independent = Activator.CreateInstance(type, nonPublic: true)!;
        var rotated = ((Vector2, Vector2, float))resolve.Invoke(independent,
            [new Vector2(10, 20), Vector2.One, 0f, new Vector2(5, 5), new Transform2D(Mathf.Pi / 2, new Vector2(100, 200))])!;
        Require(rotated.Item1.IsEqualApprox(new(70, 210)) && Mathf.IsEqualApprox(rotated.Item3, Mathf.Pi / 2),
            "新面板不能继承旧面板的定位状态，并须保留旋转和轴心。");
    }

    private static void CheckFade()
    {
        var type = typeof(Entry).Assembly.GetType("STS2SkinChanger.Ui.ToolbarCanvasTint", true)!;
        var state = Activator.CreateInstance(type, nonPublic: true)!;
        var resolve = AccessTools.Method(type, "Resolve");
        Color Apply(float current, float inherited) => (Color)resolve.Invoke(state,
            [new Color(1, 1, 1, current), new Color(1, 1, 1, inherited)])!;
        Require(Mathf.IsEqualApprox(Apply(.5f, .5f).A, .25f), "面板应跟随选角界面的淡出。");
        Require(Mathf.IsEqualApprox(Apply(.25f, .5f).A, .25f), "继承淡出不能重复相乘。");
        Require(Mathf.IsEqualApprox(Apply(.25f, 0).A, 0), "选角界面完全淡出时面板不能残留。");
        Require(Mathf.IsEqualApprox(Apply(0, 1).A, .5f), "重新显示不能丢掉作者原始透明度。");
        Require(Mathf.IsEqualApprox(Apply(.8f, 1).A, .8f), "仍应允许作者改变面板颜色和透明度。");
    }

    private static void Patch(Harmony h, Type type, string target, string prefix) =>
        h.Patch(type.GetMethods().Single(m => m.Name == target && !m.IsGenericMethod),
            prefix: new HarmonyMethod(typeof(ProviderToolbarTests), prefix));
    private static bool SkipNative() => false;
    private static bool GetZ(ref int __result) { __result = 0; return false; }
    private static bool SetClip(Control __instance, bool value)
    {
        if (ReferenceEquals(__instance, _strip))
        {
            _stripClips = value;
            if (!value) throw new InvalidOperationException("Observed strip clipping disabled.");
        }
        return false;
    }
    private static bool GetClip(Control __instance, ref bool __result)
    { __result = ReferenceEquals(__instance, _strip) && _stripClips; return false; }
    private static bool GetParent(Node __instance, ref Node? __result)
    { __result = ReferenceEquals(__instance, _toolbar) ? _strip : null; return false; }
    private static bool GetPosition(Control __instance, ref Vector2 __result)
    { __result = ReferenceEquals(__instance, _toolbar) ? new(128, 906) : new(496, 880); return false; }
    private static bool GetSize(Control __instance, ref Vector2 __result)
    { __result = ReferenceEquals(__instance, _toolbar) ? new(224, 148) : new(928, 200); return false; }
    private static bool Promote(Control control)
    { _promoted = ReferenceEquals(control, _toolbar); return false; }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
