using System.Collections;
using System.Reflection;
using HarmonyLib;
using Godot;
using STS2SkinChanger;

internal static class RandomSkinExclusionTests
{
    private static string _path = "";
    private static bool ConfigPath(ref string __result) { __result = _path; return false; }
    private static readonly Dictionary<string, Color> StateColors = new();
    private static void CaptureStateColor(string name, Color color)
    {
        StateColors[name] = color;
    }

    internal static void Run()
    {
        var assembly = typeof(Entry).Assembly;
        var service = assembly.GetType("STS2SkinChanger.Core.SkinService", true)!;
        var excluded = AccessTools.Method(service, "IsCharacterSkinExcludedFromRandom")
            ?? throw new InvalidOperationException("缺少按角色保存的随机排除设置。");
        var toggle = AccessTools.Method(service, "ToggleCharacterSkinRandomExclusion")!;
        var filter = AccessTools.Method(service, "FilterRandomCharacterSkinCandidates")!;
        var ui = assembly.GetType("STS2SkinChanger.Ui.CharacterSkinRandomExclusionUi", true)!;
        var color = new Color(.2f, .4f, .6f, .8f);
        var faded = (Color)AccessTools.Method(ui, "ChoiceColor").Invoke(null, [color, true])!;
        Require(faded.R == color.R && faded.G == color.G && faded.B == color.B && faded.A == color.A * .5f,
            "排除状态只能将当前主题文本透明度减半，不能改变颜色或整个弹窗透明度。");
        Require((Color)AccessTools.Method(ui, "ChoiceColor").Invoke(null, [color, false])! == color,
            "恢复后必须使用未淡化的主题色。");
        var property = AccessTools.Property(service, "Config");
        var previous = property.GetValue(null);
        var previousError = AccessTools.Property(service, "LastError").GetValue(null);
        var directory = Path.Combine(Path.GetTempPath(), "sc-random-exclusion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "config.json");
        var harmony = new Harmony("sc-tests.random-exclusions");
        try
        {
            harmony.Patch(AccessTools.PropertyGetter(service, "ConfigPath"), prefix: new HarmonyMethod(typeof(RandomSkinExclusionTests), nameof(ConfigPath)));
            var config = AccessTools.Method(property.PropertyType, "Deserialize").Invoke(null, ["""
                {"Selections":{"silent":"skin:a"},"RandomCharacterSkinGroups":["silent"],
                 "RandomCharacterSkinExclusions":{"SILENT":["skin:a","SKIN:A",null,"","__random_character_skin__","__workshop__"],"silent":["skin:b"],"regent":null}}
                """])!;
            property.SetValue(null, config);
            bool Excluded(string group, string option) => (bool)excluded.Invoke(null, [group, option])!;
            bool Toggle(string group, string option) => (bool)toggle.Invoke(null, [group, option])!;
            Require(Excluded("silent", "SKIN:A") && Excluded("silent", "skin:b") && !Excluded("regent", "skin:a"),
                "排除记录必须合并大小写同名角色、去重，且不能串角色。");
            CheckInteractionColors(ui);
            Require(!Excluded("silent", "__random_character_skin__") && !Excluded("silent", "__workshop__"), "命令项不能被标记。");
            var copy = AccessTools.Method(property.PropertyType, "CloneForBundleTransaction").Invoke(config, null)!;
            ((IList)((IDictionary)copy.GetType().GetProperty("RandomCharacterSkinExclusions")!.GetValue(copy)!)["silent"]!).Clear();
            Require(Excluded("silent", "skin:a"), "事务副本不能共用排除列表。");
            Require(Toggle("silent", "skin:a") && !Excluded("silent", "skin:a"), "再次右键必须恢复。");
            Require(Toggle("silent", "__base__") && Excluded("silent", "__base__"), "原皮也是可排除的随机候选。");
            Require(!Toggle("silent", "__workshop__") && !Toggle("silent", "__random_character_skin__"), "不能右键命令项更改配置。");
            var bundle = (string)AccessTools.Method(assembly.GetType("STS2SkinChanger.Core.CharacterSkinBundlePolicy", true)!, "CreateSelectionOptionId").Invoke(null, ["test"])!;
            Require(Toggle("silent", bundle), "皮肤包必须支持独立排除。");
            AccessTools.Method(ui, "ApplyInteractionColors").Invoke(null,
                ["silent", bundle, "__base__", Colors.White, new Color(.8f, .6f, .2f, 1), (Action<string, Color>)CaptureStateColor]);
            Require(StateColors.Values.All(value => value == new Color(.8f, .6f, .2f, .5f)),
                "已排除的皮肤包和原皮在悬停/选中时应同时保留强调色和半透明。");
            StateColors.Clear();
            var pool = new[] { "skin:a", "skin:b", "__base__", bundle, "merge:a" };
            var candidates = ((IEnumerable<string>)filter.Invoke(null, ["silent", pool])!).ToArray();
            Require(candidates.SequenceEqual(new[] { "skin:a", "merge:a" }), "随机候选必须实际过滤，不能只是淡化名称。");
            var loaded = AccessTools.Method(property.PropertyType, "Load").Invoke(null, [_path])!;
            property.SetValue(null, loaded);
            Require(Excluded("silent", bundle) && Excluded("silent", "__base__"), "重启后排除设置应保留。");
            Require((string)((IDictionary)loaded.GetType().GetProperty("Selections")!.GetValue(loaded)!)["silent"]! == "skin:a",
                "右键只能改变随机池，不能热切换或禁用手动选择。");
            Require(PatchProcessor.GetOriginalInstructions(AccessTools.Method(service, "ApplyRandomCharacterSkinForNewRun"))
                .Any(i => i.operand is MethodInfo m && m == filter), "新局抽取必须使用排除过滤。");
            var snapshot = AccessTools.Method(property.PropertyType, "Deserialize").Invoke(null,
                ["""{"Selections":{"silent":"before-run"},"RandomCharacterSkinExclusions":{"silent":["skin:old"]}}"""])!;
            AccessTools.Method(property.PropertyType, "CopyRandomCharacterSkinExclusionsFrom").Invoke(snapshot, [loaded]);
            property.SetValue(null, snapshot);
            Require(Excluded("silent", bundle) && !Excluded("silent", "skin:old") &&
                (string)((IDictionary)snapshot.GetType().GetProperty("Selections")!.GetValue(snapshot)!)["silent"]! == "before-run",
                "离开皮肤包对局只应保留最新随机偏好，不能顺带带回本局临时皮肤选择。");
            foreach (var restore in new[] { "RestoreCharacterSkinBundleAfterRun", "RecoverInterruptedCharacterSkinBundleSession" })
                Require(PatchProcessor.GetOriginalInstructions(AccessTools.Method(service, restore))
                    .Any(i => i.operand is MethodInfo m && m.Name == "CopyRandomCharacterSkinExclusionsFrom"),
                    "正常退出和强退恢复都不能丢失局内调整的随机偏好：" + restore);
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            property.SetValue(null, previous);
            AccessTools.Property(service, "LastError").SetValue(null, previousError);
            Directory.Delete(directory, true);
        }
        Console.WriteLine("Random exclusion passed: per-character persistence, normalization, toggle and candidate filtering.");
    }

    private static void CheckInteractionColors(Type ui)
    {
        var apply = AccessTools.Method(ui, "ApplyInteractionColors")
            ?? throw new InvalidOperationException("单项颜色在悬停和选中时被覆盖；必须设置三种交互状态的文字颜色。");
        var normal = new Color(.2f, .4f, .6f, 1);
        var accent = new Color(.8f, .6f, .2f, 1);
        try
        {
            // Capture the emitted theme writes; run real exclusion and color policy.
            foreach (var (group, hover, selected, hoverAlpha, selectedAlpha) in new[]
            {
                ("silent", "skin:a", "skin:enabled", .5f, 1f),
                ("silent", "skin:enabled", "skin:a", 1f, .5f),
                ("silent", "skin:a", "skin:a", .5f, .5f),
                ("regent", "skin:a", "skin:a", 1f, 1f),
                (null, "skin:a", "skin:a", 1f, 1f),
                ("silent", null, "skin:a", 1f, .5f)
            })
            {
                StateColors.Clear();
                apply.Invoke(null, [group, hover, selected, normal, accent, (Action<string, Color>)CaptureStateColor]);
                Require(StateColors.Count == 3 &&
                    StateColors["font_hovered_color"] == new Color(.2f, .4f, .6f, hoverAlpha) &&
                    StateColors["font_selected_color"] == new Color(.2f, .4f, .6f, selectedAlpha) &&
                    StateColors["font_hovered_selected_color"] == new Color(.2f, .4f, .6f, hoverAlpha),
                    "悬停和选中必须各自按所在角色、所在行计算透明度，不能相互污染。");
            }
            var controls = ui.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
                .Concat(ui.GetNestedTypes(BindingFlags.NonPublic).SelectMany(t => t.GetMethods(
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)));
            var calls = controls.SelectMany(m => PatchProcessor.GetOriginalInstructions(m))
                .Select(i => i.operand).OfType<MethodInfo>().Select(m => m.Name).ToHashSet();
            Require(calls.Contains("add_MouseExited") && calls.Contains("add_ItemSelected") && calls.Contains("GetSelectedItems"),
                "鼠标离开和键盘选择也必须更新交互颜色，不能只修右键瞬间。");
        }
        finally { StateColors.Clear(); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
