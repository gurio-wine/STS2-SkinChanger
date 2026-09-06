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
        var settings = assembly.GetType("STS2SkinChanger.Core.ModThemeSettings");
        Require(settings != null, "主题需要独立配置，不得把样式实验写进皮肤选择或对局存档。");
        var normalize = settings!.GetMethod("Normalize")!;
        object Parse(string json) => normalize.Invoke(JsonSerializer.Deserialize(json, settings), null)!;
        var defaults = Parse("{}");
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
        var draft = Parse("{\"PanelOpacity\":0.24,\"ButtonColor\":\"#123456\"}");
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
