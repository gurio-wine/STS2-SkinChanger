using System.Collections;
using System.Text.Json;
using HarmonyLib;
using STS2SkinChanger;

internal static class BundlePresetTests
{
    internal static void Run()
    {
        var assembly = typeof(Entry).Assembly;
        var configType = assembly.GetType("STS2SkinChanger.Core.SkinConfig", true)!;
        var policy = assembly.GetType("STS2SkinChanger.Core.BundlePresetPolicy")
            ?? throw new InvalidOperationException("皮肤包尚未拥有独立的分类预设。");
        var config = AccessTools.Method(configType, "Deserialize").Invoke(null, ["""
            {"CharacterSkinBundles":[
              {"Id":"one","Name":"同名包","CharacterGroupId":"ironclad","CharacterOptionId":"skin:a"},
              {"Id":"two","Name":"同名包","CharacterGroupId":"silent","CharacterOptionId":"skin:b","HideSources":false}],
             "MonsterSkinCategoryGroups":{"act:one":["monster:a"]},
             "CardSkinPresets":[{"Name":"同名包","CategoryId":"silent","Selections":{"cards:silent":"skin:manual"}}]}
            """])!;
        void Sync() => AccessTools.Method(policy, "Synchronize").Invoke(null,
            [config, new[] { "ironclad", "silent", "colorless" }, new[] { "act:one" }]);
        string Json(object value) => JsonSerializer.Serialize(value, value.GetType());
        IList List(string key) => (IList)configType.GetProperty(key)!.GetValue(config)!;
        string PresetKey(object bundle) => (string)AccessTools.Method(policy, "PresetKey").Invoke(null, [bundle])!;
        Sync();
        var bundles = List("CharacterSkinBundles");
        var one = PresetKey(bundles[0]!);
        var two = PresetKey(bundles[1]!);
        Require(one != two, "不同角色的同名皮肤包必须隔离。");
        Require(List("CardSkinPresets").Count == 7 && List("MonsterSkinPresets").Count == 2,
            "每个包必须为每个分类建立专属预设，不得覆盖同名普通预设。");
        var before = Json(config);
        Sync();
        Require(Json(config) == before, "重复初始化不得重置预设内容或新增副本。");
        foreach (var preset in List("CardSkinPresets").Cast<object>().Skip(1)
                     .Concat(List("MonsterSkinPresets").Cast<object>()))
            Require((bool)preset.GetType().GetProperty("AllOriginal")!.GetValue(preset)!,
                "新生成的包预设必须是全原皮，不是空优先级自动启用所有皮肤。");
        var hidden = AccessTools.Method(policy, "HiddenSources").Invoke(null, [config, "ironclad"])!;
        Require(((IEnumerable)hidden).Cast<string>().SequenceEqual(new[] { "skin:a" }),
            "默认隐藏只应命中当前角色的包来源。");
        Require(!((IEnumerable)AccessTools.Method(policy, "HiddenSources").Invoke(null, [config, "silent"])!).Cast<string>().Any(),
            "关闭隐藏后来源皮肤必须重新可见。");
        bundles[0]!.GetType().GetProperty("Name")!.SetValue(bundles[0], "新名字");
        Sync();
        Require(PresetKey(bundles[0]!) == one, "包改名不能丢失预设引用。");
        var custom = List("CardSkinPresets").Cast<object>().First(p =>
            (string)p.GetType().GetProperty("Name")!.GetValue(p)! == one);
        custom.GetType().GetProperty("AllOriginal")!.SetValue(custom, false);
        Sync();
        Require(!(bool)custom.GetType().GetProperty("AllOriginal")!.GetValue(custom)!,
            "覆盖过的包预设不能被重新初始化为原皮。");
        var clone = AccessTools.Method(configType, "CloneForBundleTransaction").Invoke(config, null)!;
        ((IList)configType.GetProperty("CardSkinPresets")!.GetValue(clone)!).Clear();
        Require(List("CardSkinPresets").Count == 7, "包事务不得共享可变预设列表。");
        var reloaded = AccessTools.Method(configType, "Deserialize").Invoke(null, [Json(config)])!;
        Require(Json(reloaded) == Json(config), "保存重进必须保留包身份、隐藏开关和专属预设内容。");
        var allOriginal = List("CardSkinPresets").Cast<object>().First(p =>
            (bool)p.GetType().GetProperty("AllOriginal")!.GetValue(p)!);
        var cardSelections = AccessTools.Method(policy, "CardSelections")
            ?? throw new InvalidOperationException("全原皮包预设尚未阻止分类优先级回填。");
        var scoped = (IDictionary)cardSelections.Invoke(null, [allOriginal, "silent"])!;
        Require(scoped.Count == 1 && (string)scoped["cards:silent"]! == "__base__",
            "全原皮卡牌预设必须显式选择原皮，不能留下继承优先级的空选择。");
        var monster = List("MonsterSkinPresets")[0]!;
        var monsterSelections = (IDictionary)AccessTools.Method(policy, "MonsterSelections").Invoke(null,
            [monster, new[] { "monster:a", "monster:new" }])!;
        Require(monsterSelections.Count == 2 && monsterSelections.Values.Cast<string>().All(v => v == "__base__"),
            "全原皮怪物预设也必须覆盖后来新增的同地区怪物。");
        AccessTools.Method(policy, "InitializeDraft").Invoke(null, [bundles[0], new[] { "ironclad", "silent" }, new[] { "act:one" }]);
        var references = (IDictionary)bundles[0]!.GetType().GetProperty("CardPresetNames")!.GetValue(bundles[0])!;
        Require(references.Count == 2 && references.Values.Cast<string>().All(value => value == one),
            "新包所有分类默认必须是专属预设，而非不修改。");
        var service = assembly.GetType("STS2SkinChanger.Core.SkinService", true)!;
        var configProperty = AccessTools.Property(service, "Config");
        var previous = configProperty.GetValue(null);
        configProperty.SetValue(null, config);
        try
        {
            var states = ((IEnumerable)AccessTools.Method(service, "GetCardSkinPresets").Invoke(null, ["silent"])!).Cast<object>().ToArray();
            Require(states.Length == 3 && (string)states[0].GetType().GetProperty("DisplayName")!.GetValue(states[0])! == "新名字" &&
                    (string)states[2].GetType().GetProperty("Name")!.GetValue(states[2])! == "同名包",
                "列表应按包预设置顶并显示包的新名字，保留普通同名项。");
            Require(!(bool)AccessTools.Method(service, "RenameCardSkinPreset").Invoke(null, ["silent", one, "bad"])! &&
                    !(bool)AccessTools.Method(service, "DeleteCardSkinPreset").Invoke(null, ["silent", one])! &&
                    !(bool)AccessTools.Method(service, "RenameMonsterSkinPreset").Invoke(null, ["act:one", one, "bad"])! &&
                    !(bool)AccessTools.Method(service, "DeleteMonsterSkinPreset").Invoke(null, ["act:one", one])!,
                "专属预设必须在服务层也禁止单独重命名和删除。");
        }
        finally { configProperty.SetValue(null, previous); }
        var localization = assembly.GetType("STS2SkinChanger.Core.ModLocalization", true)!;
        var translations = (IDictionary)AccessTools.Field(localization, "BundlePresetTexts").GetValue(null)!;
        Require(translations.Count == 15, "新增预设说明和隐藏选项必须提供全部十五种语言。");
        bundles[1]!.GetType().GetProperty("Id")!.SetValue(bundles[1], "one");
        var duplicateReferences = (IDictionary)bundles[1]!.GetType().GetProperty("CardPresetNames")!.GetValue(bundles[1])!;
        duplicateReferences["silent"] = one;
        Sync();
        Require(PresetKey(bundles[1]!) != one && (string)duplicateReferences["silent"]! == PresetKey(bundles[1]!),
            "导入了重复包 ID 时，修复身份必须同时修复本包的专属预设引用。");
        // Remove generated duplicate-ID recovery entries from this fixture's final deletion check.
        AccessTools.Method(policy, "RemoveOwnedPresets").Invoke(null, [config, bundles[1]!]);
        AccessTools.Method(policy, "RemoveOwnedPresets").Invoke(null, [config, bundles[0]!]);
        Require(List("CardSkinPresets").Count == 4 && List("MonsterSkinPresets").Count == 1,
            "删除包只能移除自己的预设，不能影响普通同名预设或其它包。");
        Console.WriteLine("Bundle presets passed: ownership, vanilla defaults, names, persistence and transaction isolation.");
    }

    private static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }
}
