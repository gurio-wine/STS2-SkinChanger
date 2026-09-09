using System.Collections;
using System.Reflection;
using HarmonyLib;
using Godot;
using STS2SkinChanger;

internal static class RandomSkinExclusionTests
{
    private static string _path = "";
    private static bool ConfigPath(ref string __result) { __result = _path; return false; }

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
            Require(!Excluded("silent", "__random_character_skin__") && !Excluded("silent", "__workshop__"), "命令项不能被标记。");
            var copy = AccessTools.Method(property.PropertyType, "CloneForBundleTransaction").Invoke(config, null)!;
            ((IList)((IDictionary)copy.GetType().GetProperty("RandomCharacterSkinExclusions")!.GetValue(copy)!)["silent"]!).Clear();
            Require(Excluded("silent", "skin:a"), "事务副本不能共用排除列表。");
            Require(Toggle("silent", "skin:a") && !Excluded("silent", "skin:a"), "再次右键必须恢复。");
            Require(Toggle("silent", "__base__") && Excluded("silent", "__base__"), "原皮也是可排除的随机候选。");
            Require(!Toggle("silent", "__workshop__") && !Toggle("silent", "__random_character_skin__"), "不能右键命令项更改配置。");
            var bundle = (string)AccessTools.Method(assembly.GetType("STS2SkinChanger.Core.CharacterSkinBundlePolicy", true)!, "CreateSelectionOptionId").Invoke(null, ["test"])!;
            Require(Toggle("silent", bundle), "皮肤包必须支持独立排除。");
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

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
