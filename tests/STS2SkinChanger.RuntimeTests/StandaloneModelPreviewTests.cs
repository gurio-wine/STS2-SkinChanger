using System.Reflection;
using System.Text.Json;
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
            (new(10, 20, 500, 429), new(0, 0, 1920, 1080), new(511, 20, 321, 429)),
            (new(10, -24, 500, 858), new(0, 0, 1920, 1080), new(511, -24, 629, 858)),
            (new(10, 20, 500, 429), new(0, 0, 700, 1080), new(511, 20, 165, 429)),
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
        Require(!PatchProcessor.GetOriginalInstructions(AccessTools.Method(panel, "BuildInterface"))
                .Any(i => i.operand is ConstructorInfo constructor && constructor.DeclaringType == typeof(Label)),
            "本机预览不应再创建底部名称，整个框内空间都应留给模型。");
        Require(Calls(AccessTools.Method(panel, "Initialize"), "DraggableSkinControl", "AttachWithHandle"),
            "黑条必须接入现有拖动/复位交互，不能只是装饰。");
        Require(Calls(AccessTools.Method(panel, "RefreshModel"), "FrameworkModelPreview", "Refresh"),
            "本机预览必须复用隔离模型和实际像素取景流程，不能另写一套贴图/骨骼加载逻辑。");
        var contextual = assembly.GetType("STS2SkinChanger.Ui.ContextualSkinControls", true)!;
        Require(PatchProcessor.GetOriginalInstructions(AccessTools.Method(contextual, "RebuildCharacterDisplay"))
            .Count(i => i.operand is MethodInfo m && m.DeclaringType == controls && m.Name == "Refresh") == 3,
            "资源皮肤、完整 DLL 皮肤和运行时提供者三种成功切换分支都必须刷新预览。");
        Require(Calls(AccessTools.Method(contextual, "HideCharacterSelector"), controls.Name, "Hide") &&
                Calls(AccessTools.Method(panel, "OnVisibilityChanged"), panel.Name, "ClearModel"),
            "开始游戏或选角界面隐藏后必须停用并清理预览，不能让隐藏模型继续运行。");
        CheckPreviewPosition(assembly);
        CheckNestedDragging(assembly);
        CheckDocking(assembly, controls, panel);
        Console.WriteLine("Standalone model preview passed: saved docking, load suppression, hover grip, nested dragging and shared renderer.");
    }

    private static void CheckDocking(Assembly assembly, Type controls, Type panel)
    {
        var dockArea = AccessTools.Method(controls, "ResolveDockArea")
            ?? throw new InvalidOperationException("模型预览尚未提供跟随角色信息框的左侧收纳区。");
        var hit = AccessTools.Method(controls, "IsGripDocked");
        var placement = AccessTools.Method(controls, "ResolveDockLayout");
        foreach (var info in new[] { new Rect2(300, 200, 500, 400), new Rect2(150, -80, 700, 600) })
        {
            var area = (Rect2)dockArea.Invoke(null, [info])!;
            Require(area.End.X < info.Position.X && area.Size.X == 38 &&
                    Mathf.IsEqualApprox(area.GetCenter().Y, info.GetCenter().Y) &&
                    Mathf.IsEqualApprox(area.Size.Y, info.Size.Y * .8f),
                "收纳区应贴在实际信息框左边，跟随框的位置、高度和父节点坐标，不能固定在截图像素。");
            var parked = (Rect2)placement.Invoke(null, [info])!;
            var gripCenter = parked.Position + new Vector2(6, parked.Size.Y / 2);
            Require(gripCenter.IsEqualApprox(area.GetCenter()) && parked.Size.Y == info.Size.Y &&
                    (bool)hit.Invoke(null, [info, gripCenter])!,
                "再次进入选角时，已收起的拖拽条必须仍在收纳区中心且与信息框对齐。");
            foreach (var point in new[] { info.GetCenter(), area.Position - Vector2.One,
                         area.End + Vector2.One, new Vector2(float.NaN, 0) })
                Require(!(bool)hit.Invoke(null, [info, point])!, "拖出收纳区应恢复，不能把信息框本身算作收纳区。");
        }
        Require(dockArea.Invoke(null, [new Rect2()]) == null &&
                dockArea.Invoke(null, [new Rect2(0, 0, float.NaN, 400)]) == null,
            "零尺寸/无效布局不能触发收纳。");
        var shouldLoad = AccessTools.Method(controls, "ShouldLoadModel");
        foreach (var docked in new[] { false, true })
        foreach (var hasCharacter in new[] { false, true })
        foreach (var enabled in new[] { false, true })
        foreach (var visible in new[] { false, true })
            Require((bool)shouldLoad.Invoke(null, [docked, hasCharacter, enabled, visible])! ==
                    (!docked && hasCharacter && enabled && visible),
                "收起时不允许加载；拖出后仅在选角仍可见且有当前角色时恢复加载。");

        Require(Calls(AccessTools.Method(panel, "QueueRefresh"), panel.Name, "CanLoadModel") &&
                Calls(AccessTools.Method(panel, "RefreshModel"), panel.Name, "CanLoadModel") &&
                Calls(AccessTools.Method(panel, "CanLoadModel"), controls.Name, "ShouldLoadModel"),
            "刷新入队和执行时都要检查收纳状态，避免收起前排队的刷新又加载模型。");
        Require(Calls(AccessTools.Method(panel, "SetDocked"), panel.Name, "ClearModel") &&
                Calls(AccessTools.Method(panel, "SetDocked"), panel.Name, "QueueRefresh"),
            "拖入要释放已有预览，拖出要恢复当前选择，不能只是调透明度。");
        Require(!Calls(AccessTools.Method(panel, "SetDocked"), panel.Name, "Suspend") &&
                !PatchProcessor.GetOriginalInstructions(AccessTools.Method(panel, "SetDocked"))
                    .Any(i => i.opcode == System.Reflection.Emit.OpCodes.Stfld &&
                              i.operand is FieldInfo field && field.Name == "_character"),
            "收纳不能清空当前角色，否则收起时切换角色后再拖出会加载过时选择或空白。");
        var surface = assembly.GetType("STS2SkinChanger.Core.FrameworkPreviewSurface", true)!;
        Require(Calls(AccessTools.Method(surface, "Initialize"), "Node", "add_TreeExiting"),
            "离开选角也要通过原生退出信号停止预览采样，不依赖动态程序集的退出虚函数。");
        var clear = PatchProcessor.GetOriginalInstructions(AccessTools.Method(panel, "ClearModel"));
        Require(clear.FindIndex(i => i.operand is MethodInfo m && m.Name == "StopCapture") is var stop && stop >= 0 &&
                stop < clear.FindIndex(i => i.operand is MethodInfo m && m.Name == "RemoveChild"),
            "移除渲染节点前必须停止取景/逐帧采样，即使 Godot 没有调用脚本的退出虚函数也不能泄漏。");
        Require(Calls(AccessTools.Method(panel, "SavePlacement"), "SkinService", "SetCharacterModelPreviewPlacement"),
            "拖拽结束应一次保存位置和收纳状态。");
    }

    private static void CheckNestedDragging(Assembly assembly)
    {
        var drag = assembly.GetType("STS2SkinChanger.Ui.DraggableSkinControl", true)!;
        var place = AccessTools.Method(drag, "ResolveNestedPlacement")
            ?? throw new InvalidOperationException("预览位于 InfoPanel 内，拖动必须换算屏幕坐标，不能沿用父节点锚点。");
        var parent = new Transform2D(new Vector2(2, 0), new Vector2(0, 2), new Vector2(200, 100));
        var (topLeft, normalized) = ((Vector2, Vector2))place.Invoke(null,
            [new Vector2(100, 200), new Vector2(1000, 800), parent, new Vector2(.9f, .95f)])!;
        Require(topLeft.IsEqualApprox(new Vector2(300, 150)) && normalized.IsEqualApprox(new Vector2(.9f, .75f)),
            "拖拽需要包含父节点位移/缩放，并将整个预览及黑条限制在屏幕内。");
        Require(place.Invoke(null, [new Vector2(100, 200), new Vector2(1000, 800),
                    new Transform2D(Vector2.Zero, Vector2.Zero, Vector2.Zero), new Vector2(.5f, .5f)]) == null,
            "界面缩放动画中的零变换不可反算成无效节点位置。");
    }

    private static string _saved = "";
    private static void CheckPreviewPosition(Assembly assembly)
    {
        var service = assembly.GetType("STS2SkinChanger.Core.SkinService", true)!;
        var configType = assembly.GetType("STS2SkinChanger.Core.SkinConfig", true)!;
        var get = AccessTools.Method(service, "GetCharacterModelPreviewPosition")
            ?? throw new InvalidOperationException("预览拖动位置尚未独立保存。");
        var set = AccessTools.Method(service, "SetCharacterModelPreviewPosition");
        var reset = AccessTools.Method(service, "ResetCharacterModelPreviewPosition");
        var isDocked = AccessTools.Method(service, "IsCharacterModelPreviewDocked")
            ?? throw new InvalidOperationException("收纳状态尚未独立保存，重进后仍会重新加载预览。");
        var savePlacement = AccessTools.Method(service, "SetCharacterModelPreviewPlacement");
        var configField = AccessTools.Field(service, "<Config>k__BackingField");
        var loadedField = AccessTools.Field(service, "_configLoaded");
        var oldConfig = configField.GetValue(null);
        var oldLoaded = loadedField.GetValue(null);
        var boundary = new Harmony("tests.preview-position-save-boundary");
        try
        {
            boundary.Patch(AccessTools.PropertyGetter(service, "ConfigPath"),
                prefix: new HarmonyMethod(typeof(StandaloneModelPreviewTests), nameof(ConfigPath)));
            boundary.Patch(AccessTools.Method(configType, "Save"),
                prefix: new HarmonyMethod(typeof(StandaloneModelPreviewTests), nameof(CaptureSave)));
            configField.SetValue(null, JsonSerializer.Deserialize("""
                {"CharacterSkinSelectorX":0.2,"CharacterSkinSelectorY":0.3,
                 "CharacterSkinMergeX":0.7,"CharacterSkinMergeY":0.8,
                 "Selections":{"silent":"unchanged"}}
                """, configType));
            loadedField.SetValue(null, true);
            Require(get.Invoke(null, null) == null, "旧配置应使用贴近信息框的默认位置。");
            Require(!(bool)isDocked.Invoke(null, null)!, "旧配置默认展开，不能改变玩家已有预览设置。");
            set.Invoke(null, [.65f, .4f]);
            configField.SetValue(null, JsonSerializer.Deserialize(_saved, configType));
            Require(((float, float)?)get.Invoke(null, null) == (.65f, .4f), "预览位置必须保存并在重进后读取。");
            var beforeInvalid = _saved;
            set.Invoke(null, [float.NaN, .5f]);
            Require(_saved == beforeInvalid && ((float, float)?)get.Invoke(null, null) == (.65f, .4f),
                "无效拖动坐标不能写坏设置。");
            set.Invoke(null, [2f, -1f]);
            Require(((float, float)?)get.Invoke(null, null) == (1f, 0f), "保存位置要限制到归一化屏幕范围。");
            savePlacement.Invoke(null, [.3f, .4f, true]);
            configField.SetValue(null, JsonSerializer.Deserialize(_saved, configType));
            Require((bool)isDocked.Invoke(null, null)! && ((float, float)?)get.Invoke(null, null) == (.3f, .4f),
                "收起和位置必须一起持久化，重进选角/重启游戏后仍应禁止加载。");
            beforeInvalid = _saved;
            savePlacement.Invoke(null, [float.NaN, .5f, false]);
            Require(_saved == beforeInvalid && (bool)isDocked.Invoke(null, null)!,
                "无效拖出位置不能意外解除收纳。");
            savePlacement.Invoke(null, [.7f, .4f, false]);
            configField.SetValue(null, JsonSerializer.Deserialize(_saved, configType));
            Require(!(bool)isDocked.Invoke(null, null)!, "拖出后必须保存恢复显示的状态。");
            savePlacement.Invoke(null, [.3f, .4f, true]);
            reset.Invoke(null, null);
            configField.SetValue(null, JsonSerializer.Deserialize(_saved, configType));
            Require(get.Invoke(null, null) == null, "右键复位必须清除保存的位置，而非临时挪回去。");
            Require(!(bool)isDocked.Invoke(null, null)!, "右键复位还应解除收纳，回到正常预览。");
            using var json = JsonDocument.Parse(_saved);
            Require(json.RootElement.GetProperty("CharacterSkinSelectorX").GetSingle() == .2f &&
                    json.RootElement.GetProperty("CharacterSkinMergeY").GetSingle() == .8f &&
                    json.RootElement.GetProperty("Selections").GetProperty("silent").GetString() == "unchanged",
                "拖动/复位不能改写其它按钮位置或角色皮肤设置。");
        }
        finally
        {
            boundary.UnpatchAll(boundary.Id);
            configField.SetValue(null, oldConfig);
            loadedField.SetValue(null, oldLoaded);
        }
    }

    private static bool ConfigPath(ref string __result) { __result = "unused-preview-test-path"; return false; }
    private static bool CaptureSave(object __instance)
    {
        _saved = JsonSerializer.Serialize(__instance, __instance.GetType());
        return false;
    }

    private static bool Calls(MethodInfo method, string type, string name) =>
        PatchProcessor.GetOriginalInstructions(method).Any(i => i.operand is MethodInfo called &&
            called.DeclaringType?.Name == type && called.Name == name);

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
