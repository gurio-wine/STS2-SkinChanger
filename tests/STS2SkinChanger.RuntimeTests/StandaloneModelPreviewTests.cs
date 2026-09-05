using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using STS2SkinChanger;

internal static class StandaloneModelPreviewTests
{
    internal static void Run()
    {
        var assembly = typeof(Entry).Assembly;
        var controls = assembly.GetType("STS2SkinChanger.Ui.CharacterModelPreviewControls")
            ?? throw new InvalidOperationException("没有原管理器时，SC 尚未提供自己的模型预览。");
        var layout = AccessTools.Method(controls, "ResolveLayout");
        foreach (var (info, screen, expected) in new (Rect2, Rect2, Rect2?)[]
        {
            (new(10, 20, 500, 429), new(0, 0, 1920, 1080), new(534, 20, 308, 429)),
            (new(10, -24, 500, 858), new(0, 0, 1920, 1080), new(534, -24, 616, 858)),
            (new(10, 20, 500, 429), new(0, 0, 700, 1080), new(534, 20, 142, 429)),
            (new(10, 20, 500, 429), new(0, 0, 560, 1080), null),
            (new(), new(0, 0, 1920, 1080), null),
            (new(float.NaN, 0, 500, 429), new(0, 0, 1920, 1080), null)
        })
        {
            var result = (Rect2?)layout.Invoke(null, [info, screen]);
            Require(expected is { } rect ? result?.IsEqualApprox(rect) == true : result == null,
                "预览框应按左侧实际框的顶边与高度对齐，右侧留间距，不越出屏幕或使用无效边界。");
        }

        var show = AccessTools.Method(controls, "ShouldShow");
        var known = (HashSet<string>)AccessTools.Field(assembly.GetType(
            "STS2SkinChanger.Core.FrameworkCompatibilityLayer", true)!, "KnownFrameworkAssemblies").GetValue(null)!;
        var added = known.Add("thunninoiSkinManager");
        try
        {
            foreach (var state in Enum.GetValues<ModLoadState>())
            {
                Mod[] mods = [new() { path = "original", manifest = new() { id = "thunninoiSkinManager" }, state = state }];
                Require((bool)show.Invoke(null, [mods, false])! == (state != ModLoadState.Loaded),
                    "只由当前实际加载的原管理器关闭内置预览，禁用或加载失败不能隐藏：" + state);
            }
            Mod[] scOnly = [new() { path = "sc", manifest = new() { id = Entry.ModId }, state = ModLoadState.Loaded }];
            Require((bool)show.Invoke(null, [scOnly, false])! && !(bool)show.Invoke(null, [scOnly, true])!,
                "随 SC 分发的同名兼容 DLL 不是原管理器；已建立原生协作时不能出现两个预览。");
        }
        finally { if (added) known.Remove("thunninoiSkinManager"); }

        var panel = assembly.GetType("STS2SkinChanger.Ui.CharacterModelPreviewPanel", true)!;
        Require(Calls(AccessTools.Method(panel, "RefreshModel"), "FrameworkModelPreview", "Refresh"),
            "本机预览必须复用隔离模型和实际像素取景流程，不能另写一套贴图/骨骼加载逻辑。");
        var contextual = assembly.GetType("STS2SkinChanger.Ui.ContextualSkinControls", true)!;
        Require(PatchProcessor.GetOriginalInstructions(AccessTools.Method(contextual, "RebuildCharacterDisplay"))
            .Count(i => i.operand is MethodInfo m && m.DeclaringType == controls && m.Name == "Refresh") == 3,
            "资源皮肤、完整 DLL 皮肤和运行时提供者三种成功切换分支都必须刷新预览。");
        Require(Calls(AccessTools.Method(contextual, "HideCharacterSelector"), controls.Name, "Hide") &&
                Calls(AccessTools.Method(panel, "OnVisibilityChanged"), panel.Name, "ClearModel"),
            "开始游戏或选角界面隐藏后必须停用并清理预览，不能让隐藏模型继续运行。");
        Console.WriteLine("Standalone model preview passed: actual-frame alignment, native-manager exclusion, shared renderer and lifecycle.");
    }

    private static bool Calls(MethodInfo method, string type, string name) =>
        PatchProcessor.GetOriginalInstructions(method).Any(i => i.operand is MethodInfo called &&
            called.DeclaringType?.Name == type && called.Name == name);

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
