using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HarmonyLib;
using STS2SkinChanger;

internal static class BundleContentModeTests
{
    private static readonly Assembly Mod = typeof(Entry).Assembly;
    private static readonly Type Service = Mod.GetType("STS2SkinChanger.Core.SkinService", true)!;
    private static readonly Type ConfigType = Mod.GetType("STS2SkinChanger.Core.SkinConfig", true)!;
    private static readonly Type BundleType = Mod.GetType("STS2SkinChanger.Core.CharacterSkinBundle", true)!;
    private static readonly Type Policy = Mod.GetType("STS2SkinChanger.Core.CharacterSkinBundlePolicy", true)!;

    internal static void Run()
    {
        var originalConfig = AccessTools.Property(Service, "Config").GetValue(null);
        var originalCatalog = AccessTools.Property(Service, "Catalog").GetValue(null);
        try
        {
            var config = Config("""
                {"CharacterSkinBundles":[{"Id":"pack","Name":"测试包","CharacterGroupId":"silent",
                "CardMode":1,"MonsterMode":0,
                "CardModPriority":[{"OptionId":" skin:b ","Enabled":true},{"OptionId":"SKIN:B","Enabled":false},
                    {"OptionId":"skin:a","Enabled":true},{"OptionId":"skin:disabled","Enabled":false},
                    {"OptionId":"skin:missing","Enabled":true}],
                "MonsterModPriority":[{"OptionId":"skin:b","Enabled":true},{"OptionId":"skin:a","Enabled":true}],
                "CardPresetNames":{"silent":"手工卡牌"},"MonsterPresetNames":{"act:one":"手工怪物"}}],
                "MonsterSkinCategoryGroups":{"act:one":["monster:a","monster:b"],"act:two":["monster:c"]},
                "CardSkinPresets":[{"Name":"手工卡牌","CategoryId":"silent","AllOriginal":true}],
                "MonsterSkinPresets":[{"Name":"手工怪物","CategoryId":"act:one","AllOriginal":true}]}
                """);
            var bundle = Bundles(config)[0]!;
            Require(BundleType.GetProperty("CardMode") != null && Int(bundle, "CardMode") == 1 && Int(bundle, "MonsterMode") == 0,
                "卡牌和怪物应独立保存模式，读取配置时不能丢掉 Mod 优先级模式。");
            var entries = List(bundle, "CardModPriority");
            Require(entries.Count == 4 && Text(entries[0]!, "OptionId") == "skin:b" &&
                    (bool)Get(entries[0]!, "Enabled")!, "优先级去重、空白清理应保留第一次出现的顺序与启用状态。");
            var cloned = AccessTools.Method(Policy, "Clone").Invoke(null, [bundle])!;
            List(cloned, "CardModPriority").Clear();
            ((IDictionary)Get(cloned, "CardPresetNames")!)["silent"] = "更改草稿";
            Require(entries.Count == 4 && (string?)((IDictionary)Get(bundle, "CardPresetNames")!)["silent"] == "手工卡牌",
                "编辑草稿不能修改已保存的任一模式配置。");
            var reloaded = Config(Json(config));
            Require(Json(reloaded) == Json(config), "两种模式、排序和未安装来源都必须能够持久化往返。");
            var legacy = Bundles(Config("""
                {"CharacterSkinBundles":[{"Name":"老包","CharacterGroupId":"silent",
                "CardPresetNames":{"silent":"老卡牌"},"MonsterPresetNames":{"act:one":"老怪物"}}]}
                """))[0]!;
            Require(Int(legacy, "CardMode") == 0 && Int(legacy, "MonsterMode") == 0 &&
                    (string?)((IDictionary)Get(legacy, "CardPresetNames")!)["silent"] == "老卡牌",
                "旧皮肤包默认多预设，不能清掉或重新解释原引用。");
            var invalid = Bundles(Config("""
                {"CharacterSkinBundles":[{"Name":"未知模式","CharacterGroupId":"silent",
                "CardMode":77,"MonsterMode":-1,"CardModPriority":null}]}
                """))[0]!;
            Require(Int(invalid, "CardMode") == 0 && Int(invalid, "MonsterMode") == 0 && List(invalid, "CardModPriority").Count == 0,
                "未知模式与空列表应安全回退到多预设，不阻止配置加载。");

            AccessTools.Property(Service, "Config").SetValue(null, config);
            var catalog = Catalog();
            AccessTools.Property(Service, "Catalog").SetValue(null, catalog);
            var before = Json(config);
            var newBundle = Activator.CreateInstance(BundleType)!;
            var initialPriority = (IList)AccessTools.Method(Service, "GetBundleModPriority").Invoke(null, [newBundle, false])!;
            Require(initialPriority.Count == 3 && initialPriority.Cast<object>().All(e => !(bool)Get(e, "Enabled")!) &&
                    List(newBundle, "CardModPriority").Count == 0,
                "新建 Mod 模式默认关闭全部已安装来源，读取本身不修改草稿或全局。");
            var initialMonsters = (IList)AccessTools.Method(Service, "GetBundleModPriority").Invoke(null, [newBundle, true])!;
            Require(initialMonsters.Count == 2 && initialMonsters.Cast<object>().All(e => !(bool)Get(e, "Enabled")!),
                "怪物的 Mod 优先级也必须默认全关闭。");
            SetMode(newBundle, "CardMode", 1);
            Require(Resolve("ResolveBundleCardPresets", newBundle).Cast<object>().All(p =>
                    Priority(p, Text(p, "CategoryId")).All(e => e.EndsWith(":off")) &&
                    (string?)((IDictionary)Get(p, "Selections")!)["cards:" + Text(p, "CategoryId")] == "__base__"),
                "未勾选来源的新包进入 Mod 模式后应使用原版，不得隐式应用任何卡图。");
            List(newBundle, "CardModPriority").Add(initialPriority[0]);
            AccessTools.Property(initialPriority[0]!.GetType(), "Enabled").SetValue(initialPriority[0], false);
            var disabledPriority = (IList)AccessTools.Method(Service, "GetBundleModPriority").Invoke(null, [newBundle, false])!;
            Require(disabledPriority.Cast<object>().All(e => !(bool)Get(e, "Enabled")!),
                "明确全禁用的模式遇到新增来源，也不能自动开启皮肤。");
            AccessTools.Property(initialPriority[0]!.GetType(), "Enabled").SetValue(initialPriority[0], true);
            var withNewSources = (IList)AccessTools.Method(Service, "GetBundleModPriority").Invoke(null, [newBundle, false])!;
            Require((bool)Get(withNewSources[0]!, "Enabled")! &&
                    withNewSources.Cast<object>().Skip(1).All(e => !(bool)Get(e, "Enabled")!),
                "已保存的启用状态保持不变，新发现的其它来源仍默认关闭。");
            var cardPresets = Resolve("ResolveBundleCardPresets", bundle);
            Require(cardPresets.Count == 2, "Mod 优先级模式应覆盖所有有卡图的分类，不只当前角色或已有预设引用。");
            var silent = cardPresets.Cast<object>().Single(p => Text(p, "CategoryId") == "silent");
            var colorless = cardPresets.Cast<object>().Single(p => Text(p, "CategoryId") == "colorless");
            Require(Priority(silent, "silent").SequenceEqual(new[] { "skin:b:on", "skin:a:on", "skin:disabled:off" }),
                "卡牌分类应投影统一顺序，保留禁用，不能按分类已有顺序应用。");
            Require(Priority(colorless, "colorless").SequenceEqual(new[] { "skin:a:on", "skin:disabled:off" }),
                "缺少高优先级来源的分类应跳过它，且不能启用明确禁用的来源。");
            Require(Text(silent, "Name") == string.Empty &&
                    ((IDictionary)Get(silent, "Selections")!).Count == 1,
                "直接 Mod 模式不伪造保存的预设名称，也不继承全局单卡指定。");
            var monsterPresets = Resolve("ResolveBundleMonsterPresets", bundle);
            Require(monsterPresets.Count == 1 && Text(monsterPresets[0]!, "Name") == "手工怪物",
                "怪物选择多预设时，不能被卡牌的 Mod 模式改变。");
            SetMode(bundle, "MonsterMode", 1);
            monsterPresets = Resolve("ResolveBundleMonsterPresets", bundle);
            Require(monsterPresets.Count == 2 &&
                    List(monsterPresets[0]!, "FollowingGroupIds").Count == 2,
                "怪物 Mod 模式必须覆盖全部可用地区，并让该地区每只怪物跟随优先级。");
            var firstPriority = List(monsterPresets[0]!, "Priority").Cast<object>()
                .Select(e => Text(e, "OptionId") + ":" + ((bool)Get(e, "Enabled")! ? "on" : "off"));
            Require(firstPriority.SequenceEqual(new[] { "skin:b:on", "skin:a:on" }), "怪物应与卡牌使用相同的前者优先规则。");
            SetMode(bundle, "CardMode", 0);
            cardPresets = Resolve("ResolveBundleCardPresets", bundle);
            Require(cardPresets.Count == 1 && Text(cardPresets[0]!, "Name") == "手工卡牌" && entries.Count == 4,
                "切回多预设应恢复原引用，不删除另一模式的优先级。");
            SetMode(bundle, "CardMode", 1);
            SetMode(bundle, "MonsterMode", 0);
            Require(Json(config) == before, "解析两种模式只能创建本局临时设置，不得重写皮肤包或全局预设。");
            foreach (var entry in entries.Cast<object>()) AccessTools.Property(entry.GetType(), "Enabled").SetValue(entry, false);
            cardPresets = Resolve("ResolveBundleCardPresets", bundle);
            Require(cardPresets.Cast<object>().All(p => Priority(p, Text(p, "CategoryId")).All(e => e.EndsWith(":off")) &&
                    (string?)((IDictionary)Get(p, "Selections")!)["cards:" + Text(p, "CategoryId")] == "__base__"),
                "禁用所有来源必须回到原版，不能回退到全局选择或自动重新启用。");
            Console.WriteLine("Bundle content modes passed: persistence, independent modes, priority projection, legacy presets and isolation.");
        }
        finally
        {
            AccessTools.Property(Service, "Config").SetValue(null, originalConfig);
            AccessTools.Property(Service, "Catalog").SetValue(null, originalCatalog);
        }
    }

    private static object Config(string json) => AccessTools.Method(ConfigType, "Deserialize").Invoke(null, [json])!;
    private static IList Bundles(object config) => List(config, "CharacterSkinBundles");
    private static object? Get(object value, string property) => AccessTools.Property(value.GetType(), property).GetValue(value);
    private static IList List(object value, string property) => (IList)Get(value, property)!;
    private static string Text(object value, string property) => (string)Get(value, property)!;
    private static int Int(object value, string property) => Convert.ToInt32(Get(value, property));
    private static string Json(object value) => JsonSerializer.Serialize(value, value.GetType());
    private static void SetMode(object bundle, string name, int value) => BundleType.GetProperty(name)!
        .SetValue(bundle, Enum.ToObject(BundleType.GetProperty(name)!.PropertyType, value));
    private static IList Resolve(string name, object bundle) => (IList)AccessTools.Method(Service, name).Invoke(null, [bundle])!;
    private static string[] Priority(object preset, string category) => ((IEnumerable)((IDictionary)Get(preset, "CardSkinPriorities")!)[category]!)
        .Cast<object>().Select(e => Text(e, "OptionId") + ":" + ((bool)Get(e, "Enabled")! ? "on" : "off")).ToArray();

    private static object Catalog()
    {
        var type = Mod.GetType("STS2SkinChanger.Catalog.SkinCatalog", true)!;
        var catalog = RuntimeHelpers.GetUninitializedObject(type);
        foreach (var (groupName, optionName, field, specifications) in new[] {
            ("CardSkinGroup", "CardSkinOption", "_cardGroups", new[] {
                ("silent", new[] { "skin:a", "skin:b", "skin:disabled" }),
                ("colorless", new[] { "skin:a", "skin:disabled" }) }),
            ("SkinGroup", "SkinOption", "_groups", new[] {
                ("monster:a", new[] { "skin:a", "skin:b" }), ("monster:b", new[] { "skin:a" }),
                ("monster:c", new[] { "skin:a" }) }) })
        {
            var groupType = Mod.GetType("STS2SkinChanger.Catalog." + groupName, true)!;
            var optionType = Mod.GetType("STS2SkinChanger.Catalog." + optionName, true)!;
            var groups = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(groupType))!;
            foreach (var (id, options) in specifications)
            {
                var group = Activator.CreateInstance(groupType, [id, id])!;
                var constructor = optionType.GetConstructors().Single(c => c.GetParameters().Length > 2);
                foreach (var option in options)
                {
                    var args = constructor.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue :
                        p.ParameterType.IsGenericType ? Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(p.ParameterType.GenericTypeArguments)) : null).ToArray();
                    args[0] = option; args[1] = option;
                    List(group, "Options").Add(constructor.Invoke(args));
                }
                groups.Add(group);
            }
            AccessTools.Field(type, field).SetValue(catalog, groups);
        }
        var identities = AccessTools.Field(type, "_providerInstanceIdentities");
        identities.SetValue(catalog, Array.CreateInstance(identities.FieldType.GenericTypeArguments[0], 0));
        return catalog;
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
