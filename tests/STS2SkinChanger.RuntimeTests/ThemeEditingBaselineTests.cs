using System.Collections;
using System.Reflection;
using Godot;
using HarmonyLib;

internal static class ThemeEditingBaselineTests
{
    internal static void Run(Assembly assembly, Func<string, object> parse)
    {
        var type = assembly.GetType("STS2SkinChanger.Core.ModThemeSession", true)!;
        var apply = AccessTools.Method(type, "ApplyPreset");
        Require(apply != null, "应用预设必须建立独立撤销基准，不能和保存的全局主题混用。");
        var initial = parse("{}");
        var preset = parse("{\"PanelColor\":\"#123456\",\"PanelBlur\":1.5}");
        var draft = parse("{\"PanelColor\":\"#654321\",\"PanelBlur\":4}");
        var session = Activator.CreateInstance(type, initial)!;
        void Call(string name, params object[] args) => AccessTools.Method(type, name).Invoke(session, args);
        object Current() => type.GetProperty("Current")!.GetValue(session)!;
        apply!.Invoke(session, [preset]);
        Call("Preview", draft);
        Call("Revert");
        Require(Current().Equals(preset), "选择非默认预设再调节，撤销必须回该预设而非启动时的默认主题。");
        var changes = 0;
        type.GetEvent("Changed")!.AddEventHandler(session, (Action)(() => changes++));
        Call("Revert");
        Require(changes == 0, "重复撤销不能重新刷新主题。");

        var directory = Directory.CreateTempSubdirectory("sc-theme-baseline-");
        try
        {
            var path = Path.Combine(directory.FullName, "theme.json");
            Call("Preview", draft);
            Call("Save", path);
            Call("Revert");
            Require(Current().Equals(preset), "保存当前使用的主题不能改写刚选预设的撤销基准。");
            var store = assembly.GetType("STS2SkinChanger.Core.ModThemeStore", true)!;
            var restored = AccessTools.Method(store, "Load").Invoke(null, [path]);
            Require(restored!.Equals(draft), "主题面板保存仍需持久化当前调节，下次启动不能丢失。");
            var reopened = Activator.CreateInstance(type, restored)!;
            AccessTools.Method(type, "Preview").Invoke(reopened, [initial]);
            AccessTools.Method(type, "Revert").Invoke(reopened, []);
            Require(type.GetProperty("Current")!.GetValue(reopened)!.Equals(draft),
                "重启后还未选预设时，撤销应恢复本次启动的已保存主题。");

            apply.Invoke(session, [initial]);
            Call("Preview", draft);
            Call("Save", path);
            Call("Revert");
            Require(Current().Equals(initial), "选择内置凉玉后保存自定义参数，撤销仍应恢复凉玉。");
            var library = Activator.CreateInstance(assembly.GetType("STS2SkinChanger.Core.ModThemePresets", true)!,
                Path.Combine(directory.FullName, "presets.json"))!;
            var builtin = ((IEnumerable)library.GetType().GetProperty("Presets")!.GetValue(library)!).Cast<object>().First();
            Require(builtin.GetType().GetProperty("Settings")!.GetValue(builtin)!.Equals(initial),
                "主题面板保存不能覆盖内置预设。");

            // Applying a matching preset must update the baseline even without a Changed event.
            Call("Preview", preset);
            apply.Invoke(session, [preset]);
            Call("Preview", draft);
            Call("Revert");
            Require(Current().Equals(preset), "相同参数的预设选择也必须建立新基准。");
        }
        finally { directory.Delete(true); }

        var editor = assembly.GetType("STS2SkinChanger.Ui.ModThemeEditor", true)!;
        var colorRow = PatchProcessor.GetOriginalInstructions(AccessTools.Method(editor, "ColorRow"));
        Require(colorRow.Any(i => i.operand is MethodInfo m && m.DeclaringType == typeof(ColorPickerButton) && m.Name == "add_PopupClosed") &&
                !colorRow.Any(i => i.operand is MethodInfo m && m.DeclaringType == typeof(ColorPickerButton) && m.Name == "add_ColorChanged"),
            "颜色行只在关闭选取器时提交，不能在连续拖动的 ColorChanged 中刷新整套主题。");
        var textType = assembly.GetType("STS2SkinChanger.Core.ThemeText", true)!;
        Require(Enum.GetNames(textType).Contains("CoolJade"), "凉玉应使用主题专用本地化，不能改动所有皮肤的默认选项名。");
        var packs = (IDictionary)assembly.GetType("STS2SkinChanger.Core.ModThemeLocalization", true)!
            .GetField("Packs", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var index = (int)Enum.Parse(textType, "CoolJade");
        Require(packs.Count == 15 && packs.Values.Cast<string[]>().All(p => p.Length > index && !string.IsNullOrWhiteSpace(p[index])),
            "内置主题名必须覆盖全部 15 种语言。");
        Console.WriteLine("Theme edit baselines passed: selected presets, independent saves, restart fallback and popup-close color wiring.");
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
