using System.Collections;
using System.Reflection;
using Godot;
using HarmonyLib;

internal static class ModThemePresetTests
{
    internal static void Run(Assembly assembly, Func<string, object> parse)
    {
        var type = assembly.GetType("STS2SkinChanger.Core.ModThemePresets");
        Require(type != null, "主题需要独立预设存储，不能写入皮肤/对局配置。");
        var directory = Directory.CreateTempSubdirectory("sc-theme-presets-");
        try
        {
            var path = Path.Combine(directory.FullName, "presets.json");
            object Load(string file) => Activator.CreateInstance(type!, file)!;
            var library = Load(path);
            var defaults = Items(library).Single();
            Require((string)Property(defaults, "Id") == "default", "没有用户预设时必须仍有内置默认项。");
            var expectedDefault = parse("""
                {"PanelColor":"#FFFFFF","PanelOpacity":0.2,"PanelBlur":3,
                 "SelectionColor":"#FFFFFF","SelectionOpacity":0.5,"SelectionBlur":2,
                 "SelectionHoverColor":"#FFFFFF","SelectionHoverOpacity":0.5,"SelectionHoverBlur":2,
                 "ButtonColor":"#FFFFFF","ButtonOpacity":0.21,"ButtonBlur":2,"HoverColor":"#DFDFDF",
                 "DropdownColor":"#FFFFFF","DropdownOpacity":0.19,"DropdownBlur":3,
                 "DropdownHoverColor":"#DFDFDF","DropdownHoverOpacity":0.5,
                 "DropdownSelectionColor":"#FFFFFF","DropdownSelectionOpacity":0.3,
                 "DropdownSelectionHoverColor":"#DFDFDF","DropdownSelectionHoverOpacity":0.5,
                 "DropdownBorderColor":"#D3D3D3","DropdownBorderWidth":0,"DropdownCornerRadius":10,
                 "TextColor":"#FFF6E2","AccentColor":"#FFEAA9","BorderColor":"#D3D3D3","BorderWidth":0,
                 "CornerRadius":10,"FontScale":1,"TextOutline":5,"TextShadowEnabled":true,
                 "TextShadowColor":"#000000","TextShadowOpacity":0.5,"TextShadowOffsetX":2,
                 "TextShadowOffsetY":2,"TextShadowSize":3}
                """);
            Require(Property(defaults, "Settings").Equals(expectedDefault) && parse("{}").Equals(expectedDefault),
                "默认预设及首次启动必须完整采用玩家 9 月 7 日已保存的主题，而不是上一版实验默认值。");
            var firstTheme = parse("{\"PanelColor\":\"#123456\",\"DropdownBlur\":1.1}");
            var id = (string)Call(library, "Create", " 夜间 ", firstTheme)!;
            var second = (string)Call(library, "Create", "备选", parse("{\"AccentColor\":\"#ABCDEF\"}"))!;
            Require((string?)Call(library, "FindMatchingId", firstTheme, id) == id &&
                    Call(library, "FindMatchingId", parse("{\"PanelColor\":\"#998877\"}"), id) == null,
                "应用预设后才能标为已应用；继续修改参数后不能保留过期的已应用标记。");
            var duplicate = (string)Call(library, "Create", "相同主题", firstTheme)!;
            Require((string?)Call(library, "FindMatchingId", firstTheme, duplicate) == duplicate,
                "内容相同的预设应优先标记刚选择的那项，不能任意跳到第一项。");
            Call(library, "Delete", duplicate);
            Require(Items(Load(path)).Count == 3 && (string)Property(Items(library)[1], "Name") == "夜间",
                "新建须保存当前完整主题，名称修剪后可重开恢复。");
            Require((bool)Call(library, "Rename", id, "测试")! &&
                    (bool)Call(library, "Overwrite", id, parse("{\"PanelOpacity\":0.75}"))!, "重命名和覆盖必须针对同一预设。");
            var changed = Items(Load(path)).Single(p => (string)Property(p, "Id") == id);
            Require((string)Property(changed, "Name") == "测试" && (float)Property(Property(changed, "Settings"), "PanelOpacity") == .75f,
                "重命名不变 ID，覆盖应写入新主题。");
            Require(!(bool)Call(library, "Delete", "default")! && !(bool)Call(library, "Rename", "default", "x")! &&
                    !(bool)Call(library, "Overwrite", "default", firstTheme)!, "默认预设必须能恢复，不允许被覆盖、重命名或删除。");
            foreach (var name in new[] { " ", "测试" })
            {
                var rejected = false;
                try { Call(library, "Create", name, firstTheme); }
                catch (TargetInvocationException e) when (e.InnerException is ArgumentException) { rejected = true; }
                Require(rejected && Items(library).Count == 3, "空名/重名不能添加空预设或改动已有数据。");
            }
            Require((bool)Call(library, "Delete", second)! && Items(Load(path)).Count == 2, "删除只影响指定用户预设。");
            File.WriteAllText(path, "broken");
            Require(Items(Load(path)).Count == 2, "预设主文件损坏时应读取备份。");
            var failing = Load(directory.FullName);
            try { Call(failing, "Create", "写入失败", firstTheme); }
            catch (TargetInvocationException e) when (e.InnerException is IOException or UnauthorizedAccessException) { }
            Require(Items(failing).Count == 1, "磁盘写入失败不能让内存列表冒充保存成功。");
        }
        finally { directory.Delete(true); }

        var editor = assembly.GetType("STS2SkinChanger.Ui.ModThemeEditor", true)!;
        var input = AccessTools.Method(editor, "HandleInput");
        Require(!Calls(input, typeof(Control), "AcceptEvent") &&
                !PatchProcessor.GetOriginalInstructions(input).Any(i => i.operand is MethodInfo m && m.DeclaringType == editor && m.Name == "Toggle"),
            "键盘输入不能再打开主题；此监听仅保留拖动所需的鼠标处理。");
        var save = AccessTools.Method(editor, "SaveTheme");
        Require(save != null, "保存反馈必须在主题成功写盘后触发。");
        var feedback = assembly.GetType("STS2SkinChanger.Core.ThemeSaveFeedback");
        Require(feedback != null, "保存反馈需要处理重复保存和旧计时器回调。");
        var instructions = PatchProcessor.GetOriginalInstructions(save!);
        var write = instructions.FindIndex(i => i.operand is MethodInfo m && m.Name == "Save" &&
            m.DeclaringType?.FullName == "STS2SkinChanger.Core.ModThemeSession");
        var begin = instructions.FindIndex(i => i.operand is MethodInfo m && m.DeclaringType == feedback && m.Name == "Begin");
        var timer = instructions.FindIndex(i => i.operand is MethodInfo m && m.DeclaringType == typeof(SceneTree) && m.Name == "CreateTimer");
        Require(write >= 0 && begin > write && timer > begin &&
                (double)feedback!.GetField("DurationSeconds")!.GetRawConstantValue()! == 1 &&
                instructions[timer - 1].opcode == System.Reflection.Emit.OpCodes.Ldc_I4_1 &&
                instructions[timer - 2].opcode == System.Reflection.Emit.OpCodes.Ldc_I4_0 &&
                instructions[timer - 3].opcode == System.Reflection.Emit.OpCodes.Ldc_I4_1,
            "写盘后才开始一秒反馈，计时必须忽略暂停和游戏时间缩放。");
        var runtime = assembly.GetType("STS2SkinChanger.Ui.ModThemeRuntime", true)!;
        Require(Calls(AccessTools.Method(editor, "BuildPresetRows"), runtime, "Input") &&
                Calls(AccessTools.Method(editor, "BuildPresetRow"), runtime, "Input") &&
                Calls(AccessTools.Method(editor, "RefreshSaveButton"), runtime, "TextControl"),
            "预设新建/重命名输入和保存反馈都须走公共主题，不使用固定强调色。");
        var state = Activator.CreateInstance(feedback!)!;
        var old = Call(state, "Begin")!;
        var latest = Call(state, "Begin")!;
        Require(!(bool)Call(state, "Expire", old)! && (bool)Property(state, "Active"), "上次保存的计时器不能清掉新的成功反馈。");
        Require((bool)Call(state, "Expire", latest)! && !(bool)Property(state, "Active"), "最新的一秒反馈到期后应恢复保存按钮。");
        var cancelled = Call(state, "Begin")!;
        Call(state, "Cancel");
        Require(!(bool)Call(state, "Expire", cancelled)!, "关闭/重新编辑后不能被旧回调改写按钮。");
        Console.WriteLine("Theme presets passed: approved default, CRUD, persistence, recovery, failed writes and transient save feedback.");
    }

    private static List<object> Items(object library) => ((IEnumerable)Property(library, "Presets")).Cast<object>().ToList();
    private static object Property(object target, string name) => target.GetType().GetProperty(name)!.GetValue(target)!;
    private static object? Call(object target, string name, params object[] args) => AccessTools.Method(target.GetType(), name).Invoke(target, args);
    private static bool Calls(MethodBase method, Type type, string name) => PatchProcessor.GetOriginalInstructions(method)
        .Any(i => i.operand is MethodInfo m && m.DeclaringType == type && m.Name == name);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
