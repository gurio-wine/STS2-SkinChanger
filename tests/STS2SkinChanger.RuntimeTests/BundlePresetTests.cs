using System.Collections;
using System.Text.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
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
        var cardRefs = (IDictionary)bundles[0]!.GetType().GetProperty("CardPresetNames")!.GetValue(bundles[0])!;
        var monsterRefs = (IDictionary)bundles[0]!.GetType().GetProperty("MonsterPresetNames")!.GetValue(bundles[0])!;
        Require((string?)cardRefs["silent"] == one && (string?)monsterRefs["act:one"] == one,
            "旧包缺失或不修改的引用也必须回退到本包预设。");
        cardRefs["silent"] = "同名包";
        cardRefs["ironclad"] = "已删除的预设";
        cardRefs["colorless"] = string.Empty;
        monsterRefs["act:one"] = "不存在";
        Sync();
        Require((string?)cardRefs["silent"] == "同名包" && (string?)cardRefs["ironclad"] == one &&
                (string?)cardRefs["colorless"] == one && (string?)monsterRefs["act:one"] == one,
            "失效或空引用应回退本包预设，但有效的其它预设不能被覆盖。");
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
        Require(references.Values.Cast<string>().All(value => value == one),
            "新包所有分类默认必须是专属预设，而非不修改。");
        var display = AccessTools.Method(policy, "DisplayName");
        Func<string, string> characterName = id => id == "ironclad" ? "铁甲战士" : "静默猎手";
        Require((string)display.Invoke(null, [config, one, characterName])! == "铁甲战士-新名字" &&
                (string)display.Invoke(null, [config, two, characterName])! == "静默猎手-同名包" &&
                (string)display.Invoke(null, [config, "普通预设", characterName])! == "普通预设",
            "包预设应使用当前角色显示名作前缀，普通预设不加前缀。");
        var service = assembly.GetType("STS2SkinChanger.Core.SkinService", true)!;
        var configProperty = AccessTools.Property(service, "Config");
        var previous = configProperty.GetValue(null);
        configProperty.SetValue(null, config);
        try
        {
            VerifyAvailableCategories(assembly, service, config);
            var states = ((IEnumerable)AccessTools.Method(service, "GetCardSkinPresets").Invoke(null, ["silent"])!).Cast<object>().ToArray();
            Require(states.Length == 3 && ((string)states[0].GetType().GetProperty("DisplayName")!.GetValue(states[0])!).EndsWith("-新名字") &&
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
        Require(translations.Count == 15, "预设锁定提示和隐藏选项必须提供全部十五种语言。");
        bundles[1]!.GetType().GetProperty("Id")!.SetValue(bundles[1], "one");
        var duplicateReferences = (IDictionary)bundles[1]!.GetType().GetProperty("CardPresetNames")!.GetValue(bundles[1])!;
        duplicateReferences["silent"] = one;
        Sync();
        Require(PresetKey(bundles[1]!) != one && (string)duplicateReferences["silent"]! == PresetKey(bundles[1]!),
            "导入了重复包 ID 时，修复身份必须同时修复本包的专属预设引用。");
        // Remove generated duplicate-ID recovery entries from this fixture's final deletion check.
        references["silent"] = PresetKey(bundles[1]!);
        AccessTools.Method(policy, "RemoveOwnedPresets").Invoke(null, [config, bundles[1]!]);
        Require((string?)references["silent"] == one,
            "删除另一个包后，引用它的包必须立即回退自身预设。");
        AccessTools.Method(policy, "RemoveOwnedPresets").Invoke(null, [config, bundles[0]!]);
        Require(List("CardSkinPresets").Count == 4 && List("MonsterSkinPresets").Count == 1,
            "删除包只能移除自己的预设，不能影响普通同名预设或其它包。");
        Console.WriteLine("Bundle presets passed: ownership, vanilla defaults, names, persistence and transaction isolation.");
    }

    private static void VerifyAvailableCategories(Assembly assembly, Type service, object config)
    {
        var catalogProperty = AccessTools.Property(service, "Catalog");
        var previous = catalogProperty.GetValue(null);
        // Only catalog identity/options are consumed; no archive or Godot resource loading.
        var catalog = RuntimeHelpers.GetUninitializedObject(catalogProperty.PropertyType);
        (IList Groups, IList Options) CreateGroups(string groupName, string optionName, string first, string empty)
        {
            var groupType = assembly.GetType("STS2SkinChanger.Catalog." + groupName, true)!;
            var optionType = assembly.GetType("STS2SkinChanger.Catalog." + optionName, true)!;
            var groups = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(groupType))!;
            var group = Activator.CreateInstance(groupType, [first, first])!;
            groups.Add(group);
            groups.Add(Activator.CreateInstance(groupType, [empty, empty]));
            var options = (IList)groupType.GetProperty("Options")!.GetValue(group)!;
            var constructor = optionType.GetConstructors().Single(c => c.GetParameters().Length > 2);
            var args = constructor.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue :
                p.ParameterType.IsGenericType ? Activator.CreateInstance(typeof(Dictionary<,>)
                    .MakeGenericType(p.ParameterType.GenericTypeArguments)) : null).ToArray();
            args[0] = "skin:a";
            args[1] = "skin:a";
            options.Add(constructor.Invoke(args));
            return (groups, options);
        }
        var cards = CreateGroups("CardSkinGroup", "CardSkinOption", "ironclad", "colorless");
        var monsters = CreateGroups("SkinGroup", "SkinOption", "monster:a", "monster:empty");
        AccessTools.Field(catalogProperty.PropertyType, "_cardGroups").SetValue(catalog, cards.Groups);
        AccessTools.Field(catalogProperty.PropertyType, "_groups").SetValue(catalog, monsters.Groups);
        var regions = (IDictionary)config.GetType().GetProperty("MonsterSkinCategoryGroups")!.GetValue(config)!;
        regions["act:empty"] = new List<string> { "monster:empty" };
        regions["act:missing"] = new List<string> { "monster:missing" };
        var before = JsonSerializer.Serialize(config, config.GetType());
        catalogProperty.SetValue(null, catalog);
        try
        {
            string[] VisibleCards() => ((IEnumerable)AccessTools.Method(service, "GetCardPresetCategories")
                .Invoke(null, null)!).Cast<object>().Select(category =>
                    (string)category.GetType().GetProperty("Id")!.GetValue(category)!).ToArray();
            string[] VisibleMonsters() => ((IEnumerable)AccessTools.Method(service, "GetBundleMonsterCategoryIds")
                .Invoke(null, null)!).Cast<string>().ToArray();
            Require(VisibleCards().SequenceEqual(new[] { "ironclad" }) &&
                    VisibleMonsters().SequenceEqual(new[] { "act:one" }),
                "分类是否可见必须取决于可用皮肤，不能因有包预设就显示空分类。");
            var skin = cards.Options[0];
            cards.Options.Clear();
            Require(VisibleCards().Length == 0, "皮肤暂时不可用时必须隐藏分类。");
            cards.Options.Add(skin);
            Require(VisibleCards().SequenceEqual(new[] { "ironclad" }) &&
                    JsonSerializer.Serialize(config, config.GetType()) == before,
                "皮肤重新可用时恢复分类；隐藏显示过程不得删除预设内容。");
        }
        finally
        {
            catalogProperty.SetValue(null, previous);
            regions.Remove("act:empty");
            regions.Remove("act:missing");
        }
    }

    private static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }
}
