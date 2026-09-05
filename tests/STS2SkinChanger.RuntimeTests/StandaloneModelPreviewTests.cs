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
        Console.WriteLine("Standalone model preview passed: compact grip spacing, independent saved position, native-manager exclusion and shared renderer.");
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
            set.Invoke(null, [.65f, .4f]);
            configField.SetValue(null, JsonSerializer.Deserialize(_saved, configType));
            Require(((float, float)?)get.Invoke(null, null) == (.65f, .4f), "预览位置必须保存并在重进后读取。");
            var beforeInvalid = _saved;
            set.Invoke(null, [float.NaN, .5f]);
            Require(_saved == beforeInvalid && ((float, float)?)get.Invoke(null, null) == (.65f, .4f),
                "无效拖动坐标不能写坏设置。");
            set.Invoke(null, [2f, -1f]);
            Require(((float, float)?)get.Invoke(null, null) == (1f, 0f), "保存位置要限制到归一化屏幕范围。");
            reset.Invoke(null, null);
            configField.SetValue(null, JsonSerializer.Deserialize(_saved, configType));
            Require(get.Invoke(null, null) == null, "右键复位必须清除保存的位置，而非临时挪回去。");
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
