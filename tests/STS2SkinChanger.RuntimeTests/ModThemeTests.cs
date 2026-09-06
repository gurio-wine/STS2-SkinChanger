using System.Reflection;
using System.Collections;
using System.Text.Json;
using STS2SkinChanger;

internal static class ModThemeTests
{
    internal static void Run()
    {
        var assembly = typeof(Entry).Assembly;
        VerifyNativeLifecycleWiring(assembly);
        VerifyBackdropCoordinates(assembly);
        VerifyInputThemeWiring(assembly);
        DragHandleHoverTests.Run();
        var settings = assembly.GetType("STS2SkinChanger.Core.ModThemeSettings");
        Require(settings != null, "主题需要独立配置，不得把样式实验写进皮肤选择或对局存档。");
        var normalize = settings!.GetMethod("Normalize")!;
        object Parse(string json) => normalize.Invoke(JsonSerializer.Deserialize(json, settings), null)!;
        var defaults = Parse("{}");
        ModThemeInteractionTests.Run(assembly, Parse);
        VerifyDropdownBlur(assembly, Parse);
        VerifyDropdownIsolation(assembly, Parse);
        VerifyBlurSampling(assembly);
        Require(settings.GetProperty("ButtonBlur") != null && settings.GetProperty("TextShadowEnabled") != null,
            "按钮背景必须有独立模糊，文字阴影必须可以在主题中调节。");
        var effects = Parse("{\"ButtonBlur\":100,\"TextShadowEnabled\":true,\"TextShadowColor\":\"invalid\",\"TextShadowOpacity\":3,\"TextShadowOffsetX\":-100,\"TextShadowOffsetY\":100,\"TextShadowSize\":99}");
        Require((float)Property(effects, "ButtonBlur") == 5 && (bool)Property(effects, "TextShadowEnabled") &&
                (string)Property(effects, "TextShadowColor") == "#000000" && (float)Property(effects, "TextShadowOpacity") == 1 &&
                (int)Property(effects, "TextShadowOffsetX") == -12 && (int)Property(effects, "TextShadowOffsetY") == 12 &&
                (int)Property(effects, "TextShadowSize") == 8, "模糊和阴影参数必须归一化，避免无界渲染开销。");
        var tint = assembly.GetType("STS2SkinChanger.Ui.ModThemeRuntime", true)!
            .GetMethod("ButtonTint", BindingFlags.NonPublic | BindingFlags.Static);
        Require(tint != null, "按钮状态必须统一计算本层颜色，不预先覆盖/累加面板的不透明度。");
        var layerTheme = Parse("{\"PanelOpacity\":0.2,\"ButtonOpacity\":0.3,\"SelectionOpacity\":0.4,\"ButtonColor\":\"#123456\",\"HoverColor\":\"#654321\"}");
        var normal = (Godot.Color)tint!.Invoke(null, [layerTheme, false, false, false])!;
        var hover = (Godot.Color)tint.Invoke(null, [layerTheme, true, false, false])!;
        var pressed = (Godot.Color)tint.Invoke(null, [layerTheme, true, true, false])!;
        var disabled = (Godot.Color)tint.Invoke(null, [layerTheme, false, false, true])!;
        Require(Math.Abs(normal.A - .3f) < .0001f && Math.Abs(hover.A - .3f) < .0001f &&
                Math.Abs(pressed.A - .4f) < .0001f && Math.Abs(disabled.A - .12f) < .0001f && normal.R != hover.R,
            "普通、悬停、按下与禁用只更改按钮本层，面板应由下层绘制保留。");
        Require(Math.Abs(new Godot.Color(1, 1, 1, .2f).Blend(normal).A - .44f) < .0001f,
            "20% 面板上叠加 30% 按钮应得到 44%，不是覆盖成 30% 或直接相加 50%。");
        var invalid = Parse("{\"PanelColor\":\"bad\",\"PanelOpacity\":-2,\"SelectionOpacity\":4,\"CornerRadius\":-3,\"FontScale\":9}");
        Require((string)Property(defaults, "PanelColor") == "#FFFFFF" && (float)Property(defaults, "PanelOpacity") < .3f,
            "初版应是更透明的白色背景。");
        Require((string)Property(invalid, "PanelColor") == "#FFFFFF" && (float)Property(invalid, "PanelOpacity") == 0 &&
                (float)Property(invalid, "SelectionOpacity") == 1 && (int)Property(invalid, "CornerRadius") == 0 &&
                (float)Property(invalid, "FontScale") <= 1.5f, "非法主题值必须归一化，不能写入渲染资源。");
        var sessionType = assembly.GetType("STS2SkinChanger.Core.ModThemeSession", true)!;
        var session = Activator.CreateInstance(sessionType, defaults)!;
        var changes = 0;
        Action listener = () => changes++;
        sessionType.GetEvent("Changed")!.AddEventHandler(session, listener);
        var draft = Parse("{\"PanelOpacity\":0.24,\"ButtonColor\":\"#123456\",\"ButtonBlur\":1.5,\"DropdownBlur\":2.25,\"TextShadowEnabled\":true,\"TextShadowOffsetX\":-2}");
        sessionType.GetMethod("Preview")!.Invoke(session, [draft]);
        sessionType.GetMethod("Preview")!.Invoke(session, [draft]);
        Require(changes == 1 && (string)Property(Property(session, "Current"), "ButtonColor") == "#123456",
            "调节即时广播，相同值不重复刷新组件。");
        var localization = assembly.GetType("STS2SkinChanger.Core.ModThemeLocalization", true)!;
        var packs = (IDictionary)localization.GetField("Packs", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var textCount = Enum.GetValues(assembly.GetType("STS2SkinChanger.Core.ThemeText", true)!).Length;
        Require(packs.Count == 15 && packs.Values.Cast<string[]>().All(pack =>
            pack.Length == textCount && pack.All(text => !string.IsNullOrWhiteSpace(text))),
            "全部 15 种语言都必须包含每个主题参数和编辑操作。");
        var harmony = new HarmonyLib.Harmony("Gurio.SkinChanger.Tests.Theme");
        try
        {
            harmony.CreateClassProcessor(assembly.GetType("STS2SkinChanger.Ui.ModThemeEditorReadyPatch", true)!).Patch();
            var ready = HarmonyLib.AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu), "_Ready");
            Require(HarmonyLib.Harmony.GetPatchInfo(ready)?.Owners.Contains(harmony.Id) == true,
                "正式版和测试版都必须能安装主题调节入口。");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
        var temp = Directory.CreateTempSubdirectory("sc-theme-");
        try
        {
            var path = Path.Combine(temp.FullName, "theme.json");
            sessionType.GetMethod("Save")!.Invoke(session, [path]);
            var store = assembly.GetType("STS2SkinChanger.Core.ModThemeStore", true)!;
            var restored = store.GetMethod("Load")!.Invoke(null, [path])!;
            Require((string)Property(restored, "ButtonColor") == "#123456", "保存并重开必须保留主题草稿。");
            Require((float)Property(restored, "DropdownBlur") == 2.25f, "下拉模糊强度必须独立保存并恢复。");
            VerifyDropdownMigration(store, temp.FullName);
            Require((float)Property(restored, "ButtonBlur") == 1.5f && (bool)Property(restored, "TextShadowEnabled") &&
                    (int)Property(restored, "TextShadowOffsetX") == -2, "新阴影/按钮模糊设置保存后必须完整恢复。");
            sessionType.GetMethod("Reset")!.Invoke(session, null);
            Require((string)Property(Property(session, "Current"), "ButtonColor") != "#123456", "恢复默认立即预览。");
            sessionType.GetMethod("Revert")!.Invoke(session, null);
            Require((string)Property(Property(session, "Current"), "ButtonColor") == "#123456", "撤销回到已保存主题。");
            File.WriteAllText(path, "invalid json");
            restored = store.GetMethod("Load")!.Invoke(null, [path])!;
            Require((string)Property(restored, "ButtonColor") == "#123456", "主主题文件损坏须恢复备份。");
            sessionType.GetMethod("Preview")!.Invoke(session, [defaults]);
            try { sessionType.GetMethod("Save")!.Invoke(session, [temp.FullName]); }
            catch (TargetInvocationException) { }
            sessionType.GetMethod("Revert")!.Invoke(session, null);
            Require((string)Property(Property(session, "Current"), "ButtonColor") == "#123456", "保存失败不能把草稿冒充已保存主题。");
        }
        finally { temp.Delete(true); }
        Console.WriteLine("Live theme passed: white defaults, normalization, preview, persistence, rollback and recovery.");
    }

    private static object Property(object target, string name) => target.GetType().GetProperty(name)!.GetValue(target)!;

    private static void VerifyDropdownBlur(Assembly assembly, Func<string, object> parse)
    {
        var theme = parse("{\"PanelBlur\":5,\"ButtonBlur\":4,\"DropdownBlur\":2.25}");
        Require(theme.GetType().GetProperty("DropdownBlur") != null, "下拉主题缺少独立模糊参数。");
        Require((float)Property(theme, "DropdownBlur") == 2.25f &&
                (float)Property(parse("{\"PanelBlur\":5}"), "DropdownBlur") == 0 &&
                (float)Property(parse("{\"DropdownBlur\":-1}"), "DropdownBlur") == 0 &&
                (float)Property(parse("{\"DropdownBlur\":100}"), "DropdownBlur") == 5,
            "下拉模糊不能继承面板；0 关闭，过大/负值需归一化。");
        var type = assembly.GetType("STS2SkinChanger.Ui.ModThemeDropdownBackdrop");
        Require(type != null, "下拉模糊需要从承载菜单的游戏视口取背景，不能在菜单内模糊文字。");
        var placement = HarmonyLib.AccessTools.Method(type, "Placement");
        var final = new Godot.Transform2D(new(1.5f, 0), new(0, 1.5f), new(20, 10));
        var (embeddedPosition, embeddedLayer) = ((Godot.Vector2, Godot.Transform2D))placement.Invoke(null,
            [true, new Godot.Vector2(400, 260), new Godot.Vector2(100, 80), final])!;
        Require(embeddedPosition == new Godot.Vector2(400, 260) && embeddedLayer == Godot.Transform2D.Identity,
            "嵌入式菜单坐标已经是承载视口坐标，不能再次除以游戏窗口缩放。");
        var (nativePosition, nativeLayer) = ((Godot.Vector2, Godot.Transform2D))placement.Invoke(null,
            [false, new Godot.Vector2(1340, 380), new Godot.Vector2(100, 80), final])!;
        Require((final * nativeLayer * nativePosition).DistanceTo(new Godot.Vector2(1240, 300)) < .001f,
            "独立窗口需要减去游戏窗口位置并抵消内容缩放，不能读到另一块屏幕区域。");
        Require(Calls(HarmonyLib.AccessTools.Method(type, "StartFollowing"), typeof(Godot.RenderingServer), "add_FramePreDraw") &&
                Calls(HarmonyLib.AccessTools.Method(type, "Release"), typeof(Godot.RenderingServer), "remove_FramePreDraw") &&
                Calls(HarmonyLib.AccessTools.Method(type, "Release"), typeof(Godot.Node), "QueueFree"),
            "背景只在菜单打开时更新，关闭/离树后必须停掉绘制监听并释放背景层。");
        var runtime = assembly.GetType("STS2SkinChanger.Ui.ModThemeRuntime", true)!;
        Require(Calls(HarmonyLib.AccessTools.Method(runtime, "Popup"), type!, "Attach"),
            "所有公共皮肤下拉入口都必须接入模糊，而不只是带皮肤包的彩色列表。");
    }

    private static void VerifyDropdownIsolation(Assembly assembly, Func<string, object> parse)
    {
        var runtime = assembly.GetType("STS2SkinChanger.Ui.ModThemeRuntime", true)!;
        var tint = HarmonyLib.AccessTools.Method(runtime, "DropdownTint");
        Require(tint != null, "下拉列表必须使用独立主题，不能读取面板/普通按钮的颜色与透明度。");
        const string dropdown = "\"DropdownColor\":\"#123456\",\"DropdownOpacity\":0.6,\"DropdownHoverColor\":\"#456789\",\"DropdownHoverOpacity\":0.2,\"DropdownSelectionColor\":\"#789ABC\",\"DropdownSelectionOpacity\":0.7";
        var first = parse("{" + dropdown + ",\"PanelColor\":\"#FFFFFF\",\"PanelOpacity\":0.1}");
        var second = parse("{" + dropdown + ",\"PanelColor\":\"#000000\",\"PanelOpacity\":1,\"HoverColor\":\"#FFFF00\",\"SelectionOpacity\":0}");
        foreach (var (hovered, selected, expected) in new[]
        {
            (false, false, new Godot.Color(new Godot.Color("123456"), .6f)),
            (true, false, new Godot.Color(new Godot.Color("456789"), .2f)),
            (false, true, new Godot.Color(new Godot.Color("789abc"), .7f))
        })
        {
            Require((Godot.Color)tint!.Invoke(null, [first, hovered, selected])! == expected &&
                    (Godot.Color)tint.Invoke(null, [second, hovered, selected])! == expected,
                "修改面板/普通按钮/图鉴选中项后，下拉背景和各状态必须保持自身配置。");
        }
        var invalid = parse("{\"DropdownOpacity\":-1,\"DropdownHoverOpacity\":4,\"DropdownSelectionOpacity\":-2,\"DropdownCornerRadius\":99,\"DropdownBorderWidth\":-2,\"DropdownColor\":\"bad\"}");
        Require((float)Property(invalid, "DropdownOpacity") == 0 && (float)Property(invalid, "DropdownHoverOpacity") == 1 &&
                (float)Property(invalid, "DropdownSelectionOpacity") == 0 && (int)Property(invalid, "DropdownCornerRadius") == 24 &&
                (int)Property(invalid, "DropdownBorderWidth") == 0 && (string)Property(invalid, "DropdownColor") == "#FFFFFF",
            "下拉配置同样要处理非法颜色、透明度、边框与圆角。");
    }

    private static void VerifyDropdownMigration(Type store, string directory)
    {
        var path = System.IO.Path.Combine(directory, "legacy-dropdown.json");
        File.WriteAllText(path, "{\"PanelColor\":\"#123456\",\"PanelOpacity\":0.47,\"HoverColor\":\"#654321\",\"ButtonOpacity\":0.81,\"CornerRadius\":7,\"DropdownOpacity\":0.3}");
        var loaded = store.GetMethod("Load")!.Invoke(null, [path])!;
        Require((string)Property(loaded, "DropdownColor") == "#123456" &&
                (float)Property(loaded, "DropdownOpacity") == .3f && (float)Property(loaded, "DropdownHoverOpacity") == .81f &&
                (int)Property(loaded, "DropdownCornerRadius") == 7,
            "旧主题只初始化缺失的下拉设置，保留已有独立值，不能强制重置玩家调好的主题。");
        store.GetMethod("Save")!.Invoke(null, [path, loaded]);
        var saved = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        saved["PanelColor"] = "#FFFFFF";
        saved["CornerRadius"] = 0;
        File.WriteAllText(path, saved.ToJsonString());
        loaded = store.GetMethod("Load")!.Invoke(null, [path])!;
        Require((string)Property(loaded, "DropdownColor") == "#123456" && (int)Property(loaded, "DropdownCornerRadius") == 7,
            "迁移保存后更改面板不能再次覆盖下拉设置。");
    }

    private static void VerifyBlurSampling(Assembly assembly)
    {
        var sampling = HarmonyLib.AccessTools.Method(assembly.GetType("STS2SkinChanger.Ui.ModThemeBackdrop", true)!, "BlurSampling");
        Require(sampling != null, "高斯模糊需要按采样层级扩大取屏边距，不能沿用九点偏移的边距。");
        foreach (var (input, expectedLod, minimumPadding) in new[]
        {
            (0f, 0f, 2f), (1f, 1f, 6f), (2.5f, 2.5f, 30f), (5f, 5f, 126f),
            (100f, 5f, 126f), (float.NaN, 0f, 2f)
        })
        {
            var (lod, padding) = ((float, float))sampling!.Invoke(null, [input])!;
            Require(lod == expectedLod && padding >= minimumPadding && padding <= 130,
                "小数模糊强度应平滑插值，取屏边距需覆盖高斯级联，异常参数不能制造无界开销。");
        }
    }

    private static void VerifyBackdropCoordinates(Assembly assembly)
    {
        var type = assembly.GetType("STS2SkinChanger.Ui.ModThemeBackdrop", true)!;
        var geometry = HarmonyLib.AccessTools.Method(type, "CopyGeometry");
        Require(geometry != null, "模糊必须使用当前视口的实际取屏区域，不能把局部矩形当屏幕矩形。");
        foreach (var transform in new[]
        {
            new Godot.Transform2D(0, new Godot.Vector2(1500, 100)),
            new Godot.Transform2D(new(1.5f, 0), new(0, 1.5f), new(320, 180)),
            new Godot.Transform2D(.3f, new Godot.Vector2(600, 260))
        })
        {
            var size = new Godot.Vector2(180, 40);
            var (rect, copyTransform) = ((Godot.Rect2, Godot.Transform2D))geometry!.Invoke(null, [size, transform, 8f])!;
            var points = new[] { Godot.Vector2.Zero, new Godot.Vector2(size.X, 0), size, new Godot.Vector2(0, size.Y) }
                .Select(point => transform * point).ToArray();
            Require(Math.Abs(rect.Position.X - (MathF.Floor(points.Min(p => p.X)) - 8)) < .001f &&
                    Math.Abs(rect.End.Y - (MathF.Ceiling(points.Max(p => p.Y)) + 8)) < .001f &&
                    points.All(rect.HasPoint), "平移、缩放、旋转后的取屏矩形必须覆盖实际控件，并在屏幕像素中留出采样边距。");
            var identity = transform * copyTransform;
            Require(identity.X.DistanceTo(Godot.Vector2.Right) < .001f &&
                    identity.Y.DistanceTo(Godot.Vector2.Down) < .001f && identity.Origin.Length() < .001f,
                "取屏节点本身必须使用视口坐标，避免不同渲染后端再次变换矩形。");
        }
        Require(Calls(HarmonyLib.AccessTools.Method(type, "SyncCopy"), typeof(Godot.CanvasItem), "GetViewportTransform") &&
                Calls(HarmonyLib.AccessTools.Method(type, "SyncCopy"), typeof(Godot.CanvasItem), "GetGlobalTransform"),
            "取屏坐标必须包含视口缩放和父级变换，不能只取初始位置。");
        Require(Calls(HarmonyLib.AccessTools.Method(type, "TrackCopy"), typeof(Godot.RenderingServer), "add_FramePreDraw") &&
                Calls(HarmonyLib.AccessTools.Method(type, "StopTrackingCopy"), typeof(Godot.RenderingServer), "remove_FramePreDraw"),
            "可见模糊层在绘制前跟随移动；隐藏或离树后必须停止更新。");
        var constructor = type.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
        Require(HarmonyLib.PatchProcessor.GetOriginalInstructions(constructor).Count(i =>
                i.operand is MethodInfo called && called.DeclaringType == typeof(Godot.Node) && called.Name == "add_TreeEntered") == 2,
            "离树创建的控件必须同时接入父项和背景的原生入树事件，不能因子项尚未入树而永远停用模糊。");
    }

    private static bool Calls(MethodBase method, Type type, string name) =>
        HarmonyLib.PatchProcessor.GetOriginalInstructions(method).Any(i => i.operand is MethodInfo called &&
            called.DeclaringType == type && called.Name == name);

    private static void VerifyInputThemeWiring(Assembly assembly)
    {
        var runtime = assembly.GetType("STS2SkinChanger.Ui.ModThemeRuntime", true)!;
        Require(HarmonyLib.AccessTools.Method(runtime, "Input") != null,
            "命名输入框需要完整的公共主题接入，不能只改字体/焦点边框。");
        var composition = assembly.GetType("STS2SkinChanger.Ui.CharacterSkinCompositionControls", true)!;
        Require(Calls(HarmonyLib.AccessTools.Method(composition, "ApplyLineEditTheme"), runtime, "Input"),
            "皮肤合并和皮肤包的命名框必须采用公共主题。");
        foreach (var (name, factory) in new[] { ("CardSkinControls", "BuildPresetOverlay"),
                     ("ContextualSkinControls", "BuildMonsterPresetOverlay") })
        {
            var owner = assembly.GetType("STS2SkinChanger.Ui." + name, true)!;
            Require(HarmonyLib.PatchProcessor.GetOriginalInstructions(HarmonyLib.AccessTools.Method(owner, factory))
                    .Count(i => i.operand is MethodInfo called && called.DeclaringType == runtime && called.Name == "Input") == 2,
                name + " 的新建/重命名预设框必须显式接入公共主题。");
        }
    }

    private static void VerifyNativeLifecycleWiring(Assembly assembly)
    {
        // No Godot host in this test: inspect the actual registration boundary, not just whether
        // the main-menu Harmony patch installs. Plain mod DLLs have no generated virtual bridge.
        var editor = assembly.GetType("STS2SkinChanger.Ui.ModThemeEditor", true)!;
        var ensure = editor.GetMethod("Ensure")!;
        var calls = HarmonyLib.PatchProcessor.GetOriginalInstructions(ensure)
            .Select(i => i.operand).OfType<MethodInfo>().ToArray();
        Require(calls.Any(m => m.DeclaringType == editor && m.Name == "Initialize"),
            "主题入口必须明确初始化窗口，不能依赖未注册的 _Ready，否则点击会空引用。");
        var connect = editor.GetMethod("Connect", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var disconnect = editor.GetMethod("Disconnect", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Require(Calls(connect, "add_WindowInput") && Calls(disconnect, "remove_WindowInput"),
            "主题快捷键必须连接原生输入事件，并在退出时解绑。");
        var binding = assembly.GetType("STS2SkinChanger.Ui.ModThemeBinding", true)!;
        var constructor = binding.GetConstructor(Type.EmptyTypes)!;
        Require(Calls(constructor, "add_TreeEntered") && Calls(constructor, "add_TreeExiting"),
            "实时主题刷新必须由原生入树/退树事件管理，否则能打开但无法即时应用。");

        static bool Calls(MethodBase method, string name) =>
            HarmonyLib.PatchProcessor.GetOriginalInstructions(method)
                .Any(i => i.operand is MethodInfo target && target.Name == name);
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
