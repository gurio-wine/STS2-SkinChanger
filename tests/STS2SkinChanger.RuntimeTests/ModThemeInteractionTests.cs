using System.Reflection;
using Godot;
using HarmonyLib;

internal static class ModThemeInteractionTests
{
    internal static void Run(Assembly assembly, Func<string, object> parse)
    {
        var theme = parse("""
            {"SelectionColor":"#112233","SelectionOpacity":0.2,"SelectionHoverColor":"#445566",
             "SelectionHoverOpacity":0.7,"SelectionHoverBlur":2,"DropdownSelectionColor":"#778899",
             "DropdownSelectionHoverColor":"#AABBCC","DropdownSelectionHoverOpacity":0.6}
            """);
        Require(theme.GetType().GetProperty("SelectionHoverColor") != null,
            "选中项需要独立的悬停颜色，不能选中后就丢失悬停状态。");
        var runtime = assembly.GetType("STS2SkinChanger.Ui.ModThemeRuntime", true)!;
        var buttonTint = AccessTools.Method(runtime, "ButtonTint");
        Require((Color)buttonTint.Invoke(null, [theme, true, true, false])! == new Color(new Color("445566"), .7f) &&
                (Color)buttonTint.Invoke(null, [theme, false, true, false])! == new Color(new Color("112233"), .2f),
            "选中悬停和只选中必须各用各的颜色及透明度。");
        var dropdownTint = AccessTools.Method(runtime, "DropdownTint");
        Require((Color)dropdownTint.Invoke(null, [theme, true, true])! == new Color(new Color("aabbcc"), .6f),
            "下拉的选中悬停状态必须独立，不能落回图鉴或原生默认样式。");
        var invalid = parse("""
            {"SelectionHoverColor":"invalid","SelectionHoverOpacity":-2,"SelectionHoverBlur":99,
             "DropdownSelectionHoverColor":"invalid","DropdownSelectionHoverOpacity":99}
            """);
        Require((string)Property(invalid, "SelectionHoverColor") == "#FFFFFF" &&
                (float)Property(invalid, "SelectionHoverOpacity") == 0 &&
                (float)Property(invalid, "SelectionHoverBlur") == 5 &&
                (float)Property(invalid, "DropdownSelectionHoverOpacity") == 1,
            "新增状态也必须规范化非法颜色、透明度和模糊，不能绕过主题约束。");
        var store = assembly.GetType("STS2SkinChanger.Core.ModThemeStore", true)!;
        var directory = Directory.CreateTempSubdirectory("sc-theme-hover-");
        try
        {
            var path = Path.Combine(directory.FullName, "theme.json");
            store.GetMethod("Save")!.Invoke(null, [path, theme]);
            var loaded = store.GetMethod("Load")!.Invoke(null, [path])!;
            Require((Color)dropdownTint.Invoke(null, [loaded, true, true])! == new Color(new Color("aabbcc"), .6f) &&
                    (Color)buttonTint.Invoke(null, [loaded, true, true, false])! == new Color(new Color("445566"), .7f),
                "重开后选中悬停配置须保留，不能重新继承普通悬停/选中。");
            File.WriteAllText(path, "{\"SelectionColor\":\"#112233\",\"SelectionOpacity\":0.17,\"SelectionBlur\":1.2,\"DropdownHoverColor\":\"#AABBCC\",\"DropdownHoverOpacity\":0.42}");
            loaded = store.GetMethod("Load")!.Invoke(null, [path])!;
            Require((Color)buttonTint.Invoke(null, [loaded, true, true, false])! == new Color(new Color("112233"), .17f) &&
                    (float)Property(loaded, "SelectionHoverBlur") == 1.2f &&
                    (Color)dropdownTint.Invoke(null, [loaded, true, true])! == new Color(new Color("aabbcc"), .42f),
                "旧主题只初始化缺失项，沿用用户原来调好的相关颜色和透明度。");
        }
        finally { directory.Delete(true); }
        var backdrop = assembly.GetType("STS2SkinChanger.Ui.CompendiumBackdrop", true)!;
        var appearance = AccessTools.Method(backdrop, "Appearance");
        Require(appearance != null, "图鉴背景需要同时处理选择、悬停和禁用。");
        var (tint, blur, visible) = ((Color, float, bool))appearance!.Invoke(null, [theme, true, true, false])!;
        Require(tint == new Color(new Color("445566"), .7f) && blur == 2 && visible,
            "图鉴选中项悬停时必须实际应用新的颜色和模糊参数。");
        var (_, _, hidden) = ((Color, float, bool))appearance.Invoke(null, [theme, false, true, true])!;
        Require(!hidden, "禁用项不能继续画悬停背景。");

        var hover = assembly.GetType("STS2SkinChanger.Ui.ModThemeListHover", true)!;
        var padding = AccessTools.Method(hover, "ScrollPadding");
        Require(padding != null, "滚动列表必须为悬停放大和文字装饰预留空间。");
        foreach (var width in new[] { 240f, 312f, 480f })
        {
            var inset = (Vector2)padding!.Invoke(null, [new Vector2(width, 60), 12f])!;
            var expandedWidth = (width - 2 * inset.X) * 1.04f + 24;
            Require(expandedWidth <= width + .001f && inset.Y >= 13.2f,
                "放大后的内容连同描边/阴影应落在滚动窗口内，而不是关闭滚动裁剪。");
        }
        var editor = assembly.GetType("STS2SkinChanger.Ui.ModThemeEditor", true)!;
        Require(Calls(AccessTools.Method(editor, "BuildUi"), runtime, "Panel") &&
                Calls(AccessTools.Method(editor, "MakeButton"), runtime, "Button") &&
                Calls(AccessTools.Method(editor, "MakeLabel"), runtime, "TextControl") &&
                Calls(AccessTools.Method(editor, "NumberRow"), runtime, "Input"),
            "主题编辑面板、按钮、标签和数值输入都必须采用正在预览的公共主题。");
        Require(Calls(AccessTools.Method(editor, "Section"), typeof(CanvasItem), "Hide") &&
                Calls(AccessTools.Method(editor, "Section"), typeof(BaseButton), "add_Toggled"),
            "每个主题分类的内容必须初始收起，并通过原生切换事件展开/收起。");
        var shadow = assembly.GetType("STS2SkinChanger.Ui.ModThemeButtonShadow", true)!;
        var create = AccessTools.Method(shadow, "CreateShadow");
        Require(create != null && PatchProcessor.GetOriginalInstructions(create).Any(i =>
                i.operand is ConstructorInfo constructor && constructor.DeclaringType == typeof(OptionButton)),
            "下拉按钮的阴影必须复用原生下拉文字布局，不能用另一种控件猜测箭头占位和长文字截断。");
        Console.WriteLine("Theme interaction states passed: selected hover, clipped list bounds, themed folds and native button shadows.");
    }

    private static bool Calls(MethodBase method, Type owner, string name) =>
        PatchProcessor.GetOriginalInstructions(method).Any(i => i.operand is MethodInfo called &&
            called.DeclaringType == owner && called.Name == name);
    private static object Property(object target, string name) => target.GetType().GetProperty(name)!.GetValue(target)!;
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
