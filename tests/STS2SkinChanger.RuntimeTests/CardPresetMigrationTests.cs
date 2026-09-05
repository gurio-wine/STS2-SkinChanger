using System.Collections;
using System.Text.Json;
using HarmonyLib;
using STS2SkinChanger;

internal static class CardPresetMigrationTests
{
    internal static void Audit(string path)
    {
        var assembly = typeof(Entry).Assembly;
        var configType = assembly.GetType("STS2SkinChanger.Core.SkinConfig", true)!;
        var policy = assembly.GetType("STS2SkinChanger.Core.CardPresetMigrationPolicy", true)!;
        var config = AccessTools.Method(configType, "Deserialize").Invoke(null, [File.ReadAllText(path)])!;
        var presets = ((IEnumerable)configType.GetProperty("CardSkinPresets")!.GetValue(config)!).Cast<object>();
        var names = presets.Select(p => (string?)p.GetType().GetProperty("CategoryId")!.GetValue(p))
            .Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(id => id!, id => id!, StringComparer.OrdinalIgnoreCase);
        AccessTools.Method(policy, "Run").Invoke(null, [config, names, new Dictionary<string, string>()]);
        var archive = ((IEnumerable)configType.GetProperty("ArchivedLegacyCardSkinPresets")!.GetValue(config)!).Cast<object>().ToArray();
        Console.WriteLine($"Read-only migration audit: {archive.Length} archived candidates; source file not written.");
        foreach (var entry in archive)
            Console.WriteLine(entry.GetType().GetProperty("Name")!.GetValue(entry));
    }

    internal static void Run()
    {
        var assembly = typeof(Entry).Assembly;
        var configType = assembly.GetType("STS2SkinChanger.Core.SkinConfig", true)!;
        var policy = assembly.GetType("STS2SkinChanger.Core.CardPresetMigrationPolicy")
            ?? throw new InvalidOperationException("旧预设迁移仍未按实际分类内容限制范围。");
        object Load(string json) => AccessTools.Method(configType, "Deserialize").Invoke(null, [json])!;
        var names = new Dictionary<string, string> { ["ironclad"] = "铁甲战士", ["silent"] = "猎手", ["empty"] = "无皮肤角色" };
        var cards = new Dictionary<string, string> { ["cards:item:one"] = "silent" };
        void Migrate(object config) => AccessTools.Method(policy, "Run").Invoke(null, [config, names, cards]);
        IList Presets(object config) => (IList)configType.GetProperty("CardSkinPresets")!.GetValue(config)!;
        string Json(object config) => JsonSerializer.Serialize(config, configType);
        var source = Load("""
            {"CardSkinPresets":[{"Name":"旧包","CardSkinPriorities":{"ironclad":[{"OptionId":"skin:a","Enabled":true}]},
               "Selections":{"cards:item:one":"__base__"}}],"ActiveCardSkinPreset":"旧包",
             "Selections":{"cards:empty":"__base__"},"CardSkinPriorities":{"empty":[{"OptionId":"unrelated","Enabled":true}]}}
            """);
        Migrate(source);
        Require(Presets(source).Count == 2, "没有旧数据的分类不能凭当前优先级补出预设。");
        var active = (IDictionary)configType.GetProperty("ActiveCardSkinPresets")!.GetValue(source)!;
        Require(active.Count == 2 && !active.Contains("empty"), "全局启用状态只能迁移到有实际数据的分类。");
        var migrated = Json(source);
        Migrate(source);
        Require(Json(source) == migrated, "重复启动不能重复拆分或修改已迁移的数据。");

        var repaired = Load("""
            {"CardSkinPresets":[
              {"Name":"甲","CategoryId":"ironclad"}, {"Name":"乙","CategoryId":"ironclad"},
              {"Name":"丙","CategoryId":"ironclad"},
              {"Name":"无皮肤角色-甲","CategoryId":"empty","CardSkinPriorities":{"empty":[{"OptionId":"fallback","Enabled":true}]},"Selections":{"cards:empty":"__base__"}},
              {"Name":"无皮肤角色-乙","CategoryId":"empty","CardSkinPriorities":{"empty":[{"OptionId":"fallback","Enabled":true}]},"Selections":{"cards:empty":"__base__"}},
              {"Name":"无皮肤角色-丙","CategoryId":"empty","CardSkinPriorities":{"empty":[{"OptionId":"manually-changed","Enabled":true}]}},
              {"Name":"猎手-甲","CategoryId":"silent","Selections":{"cards:item:one":"skin:chosen"}}],
             "ActiveCardSkinPresets":{"empty":"无皮肤角色-甲","silent":"猎手-甲"},
             "Selections":{"cards:empty":"skin:live"},"CardSkinPriorities":{"empty":[{"OptionId":"live","Enabled":false}]}}
            """);
        var referencedJson = System.Text.Json.Nodes.JsonNode.Parse(Json(repaired))!;
        referencedJson["CharacterSkinBundles"] = System.Text.Json.Nodes.JsonNode.Parse("""
            [{"Name":"玩家指定","CharacterGroupId":"ironclad","CardPresetNames":{"empty":"无皮肤角色-甲"}}]
            """);
        var referenced = Load(referencedJson.ToJsonString());
        Migrate(referenced);
        Require(Presets(referenced).Count == 7, "玩家明确在皮肤包里引用过的预设不能按旧副本归档。");
        Migrate(repaired);
        Require(Presets(repaired).Count == 5, "应归档可识别的批量回填副本，并保留手动改过的或有单卡数据的预设。");
        var archive = (IList)configType.GetProperty("ArchivedLegacyCardSkinPresets")!.GetValue(repaired)!;
        Require(archive.Count == 2, "修复不得不可恢复地删除旧配置。");
        var selections = (IDictionary)configType.GetProperty("Selections")!.GetValue(repaired)!;
        Require((string)selections["cards:empty"]! == "skin:live", "清理错误预设不得重置玩家当前外观。");
        active = (IDictionary)configType.GetProperty("ActiveCardSkinPresets")!.GetValue(repaired)!;
        Require(!active.Contains("empty") && active.Contains("silent"), "只清理被归档副本的启用标记。");
        var once = Json(repaired);
        Migrate(repaired);
        Require(Json(repaired) == once && Json(Load(once)) == once, "修复必须幂等并保留可恢复归档。");
        Console.WriteLine("Card preset migration passed: scoped data, no fallback filling, conservative archive and idempotence.");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
