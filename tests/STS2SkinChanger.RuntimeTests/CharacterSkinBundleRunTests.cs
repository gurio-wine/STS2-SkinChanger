using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using STS2SkinChanger;

internal static class CharacterSkinBundleRunTests
{
    private static string _directory = string.Empty;
    private static bool SkipEngine() => false;
    private static bool Characters(ref IEnumerable<CharacterModel> __result) { __result = []; return false; }
    private static bool LogError(object[] __args) { if (!_failMount) Console.WriteLine("Bundle transaction log: " + __args[0]); return false; }
    private static bool _failMount;
    private static bool MountBoundary()
    {
        if (_failMount) throw new InvalidOperationException("Test resource load failure");
        return false;
    }
    private static bool ConfigPath(ref string __result) { __result = Path.Combine(_directory, "global.json"); return false; }
    private static bool RestorePath(ref string __result) { __result = Path.Combine(_directory, "restore.json"); return false; }

    internal static void Run()
    {
        var assembly = typeof(Entry).Assembly;
        var service = assembly.GetType("STS2SkinChanger.Core.SkinService", true)!;
        var resume = AccessTools.Method(service, "ResumeCharacterSkinBundleForRun");
        Require(resume != null, "读档缺少恢复本局皮肤包的入口，退出恢复全局后包预设永久失效。");
        var stateType = assembly.GetType("STS2SkinChanger.Core.CharacterSkinBundleRunState", true)!;
        var store = assembly.GetType("STS2SkinChanger.Core.CharacterSkinBundleRunStore", true)!;
        var configType = assembly.GetType("STS2SkinChanger.Core.SkinConfig", true)!;
        object Config(string json) => AccessTools.Method(configType, "Deserialize").Invoke(null, [json])!;
        object State(string identity) => JsonSerializer.Deserialize("""
            {"RunIdentity":"IDENTITY","CharacterGroupId":"silent","BundleName":"CZN",
             "Cards":[{"Name":"包卡牌","CategoryId":"silent","CardSkinPriorities":{"silent":[{"OptionId":"skin:a","Enabled":true}]},
                        "Selections":{"cards:silent":"skin:a"}}],
             "Monsters":[{"Name":"包怪物","CategoryId":"act:one","Priority":[{"OptionId":"skin:a","Enabled":true}],
                           "Selections":{"monster:a":"skin:a"},"FollowingGroupIds":["monster:a"]}]}
            """.Replace("IDENTITY", identity), stateType)!;
        _directory = Path.Combine(Path.GetTempPath(), "sc-bundle-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "run.json");
        var saved = State("run-A");
        AccessTools.Method(store, "Save").Invoke(null, [path, saved]);
        var loaded = AccessTools.Method(store, "LoadMatching").Invoke(null, [path, "run-A"]);
        Require(loaded != null, "重建进程内状态后必须能从磁盘找回该局配置。");
        Require(AccessTools.Method(store, "LoadMatching").Invoke(null, [path, "run-B"]) == null,
            "不能将另一局、角色或玩家的包预设套到当前存档。");
        var identityMethod = AccessTools.Method(stateType, "Identity");
        string Identity(long start, string seed, ulong player, ulong[] peers) =>
            (string)identityMethod.Invoke(null, [start, seed, player, peers])!;
        var identity = Identity(100, "seed", 1, [1, 2]);
        Require(identity == Identity(100, "seed", 1, [2, 1]) &&
                identity != Identity(101, "seed", 1, [1, 2]) &&
                identity != Identity(100, "other", 1, [1, 2]) &&
                identity != Identity(100, "seed", 2, [1, 2]) &&
                identity != Identity(100, "seed", 1, [1, 3]),
            "读档身份应稳定于玩家列表顺序，但必须隔离开局时间、种子、本机玩家和队伍。");

        var configProperty = AccessTools.Property(service, "Config");
        var catalogProperty = AccessTools.Property(service, "Catalog");
        var previousConfig = configProperty.GetValue(null);
        var previousCatalog = catalogProperty.GetValue(null);
        var fieldNames = new[] { "_characterSkinBundleRunSnapshot", "_characterSkinBundleRunVisualGroups", "_characterSkinBundleRunCardGroups",
            "_characterSkinBundleRunState", "_characterSkinBundleRunSavePath" };
        var previousFields = fieldNames.ToDictionary(name => name, name => AccessTools.Field(service, name).GetValue(null));
        var harmony = new Harmony("sc-tests.bundle-resume");
        try
        {
            harmony.Patch(AccessTools.PropertyGetter(typeof(ModelDb), "AllCharacters"),
                prefix: new HarmonyMethod(typeof(CharacterSkinBundleRunTests), nameof(Characters)));
            harmony.Patch(AccessTools.PropertyGetter(service, "ConfigPath"), prefix: new HarmonyMethod(typeof(CharacterSkinBundleRunTests), nameof(ConfigPath)));
            harmony.Patch(AccessTools.PropertyGetter(service, "CharacterSkinBundleRunSnapshotPath"), prefix: new HarmonyMethod(typeof(CharacterSkinBundleRunTests), nameof(RestorePath)));
            foreach (var name in new[] { "MountOverlay", "MountCardOverlay" })
                harmony.Patch(AccessTools.Method(service, name), prefix: new HarmonyMethod(typeof(CharacterSkinBundleRunTests), nameof(MountBoundary)));
            var log = assembly.GetType("STS2SkinChanger.Core.ModLog", true)!;
            foreach (var method in log.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Where(m => m.Name is "Info" or "Warn" or "Error"))
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(CharacterSkinBundleRunTests), method.Name == "Error" ? nameof(LogError) : nameof(SkipEngine)));
            var global = Config("""
                {"Selections":{"silent":"character:keep","monster:a":"__base__","cards:silent":"__base__","merchant":"merchant:keep"},
                 "CardPriorityDefaultsVersion":1,"MonsterPriorityDefaultsVersion":2,
                 "CardSkinPriorities":{"silent":[{"OptionId":"skin:a","Enabled":false}]},
                 "MonsterSkinPriorities":{"act:one":[{"OptionId":"skin:a","Enabled":false}]},
                 "MonsterSkinCategoryGroups":{"act:one":["monster:a"]},
                 "ActiveCardSkinPresets":{"silent":"全局卡牌"},"ActiveMonsterSkinPresets":{"act:one":"全局怪物"}}
                """);
            configProperty.SetValue(null, global);
            catalogProperty.SetValue(null, CreateCatalog(assembly, catalogProperty.PropertyType));
            AccessTools.Field(service, "_characterSkinBundleRunSnapshot").SetValue(null, null);
            AccessTools.Field(service, "_characterSkinBundleRunState").SetValue(null, null);
            AccessTools.Field(service, "_characterSkinBundleRunSavePath").SetValue(null, null);
            var before = JsonSerializer.Serialize(global, configType);
            Require((bool)resume!.Invoke(null, [loaded])!, "读档恢复事务必须成功。");
            var active = configProperty.GetValue(null)!;
            string? Selected(string key) => (string?)((IDictionary)configType.GetProperty("Selections")!.GetValue(active)!)[key];
            Require(Selected("cards:silent") == "skin:a" && Selected("monster:a") == "skin:a",
                "继续对局必须恢复该局的卡面和怪物，不使用当前全局选择。");
            Require(Selected("silent") == "character:keep" && Selected("merchant") == "merchant:keep",
                "恢复卡牌和怪物预设不能重新挂载角色或修改商人皮肤。");
            Require((string?)((IDictionary)configType.GetProperty("ActiveCardSkinPresets")!.GetValue(active)!)["silent"] == "包卡牌" &&
                    (string?)((IDictionary)configType.GetProperty("ActiveMonsterSkinPresets")!.GetValue(active)!)["act:one"] == "包怪物",
                "卡牌和怪物分类的当前预设名称必须与读档配置一致。");
            Require(JsonSerializer.Serialize(global, configType) == before, "本局覆盖不得修改全局配置对象。");
            AccessTools.Method(service, "RestoreCharacterSkinBundleAfterRun").Invoke(null, null);
            Require(JsonSerializer.Serialize(configProperty.GetValue(null), configType) == before,
                "再次退出必须恢复读档之前的全局预设，而不是早先开局的旧全局配置。");
            Require(AccessTools.Method(store, "LoadMatching").Invoke(null, [path, "run-A"]) != null,
                "离开对局恢复全局不应删除继续该局所需的记录。");
            Require((bool)resume.Invoke(null, [loaded])!, "同一存档应允许第二次恢复。");
            AccessTools.Field(service, "_characterSkinBundleRunSavePath").SetValue(null, path);
            active = configProperty.GetValue(null)!;
            ((IDictionary)configType.GetProperty("Selections")!.GetValue(active)!)["cards:silent"] = "__base__";
            ((IDictionary)configType.GetProperty("Selections")!.GetValue(active)!)["monster:a"] = "__base__";
            foreach (var (property, category) in new[] { ("CardSkinPriorities", "silent"), ("MonsterSkinPriorities", "act:one") })
            {
                var priorities = (IDictionary)configType.GetProperty(property)!.GetValue(active)!;
                priorities[category] = JsonSerializer.Deserialize("""[{"OptionId":"skin:a","Enabled":false}]""", priorities[category]!.GetType());
            }
            ((IList)configType.GetProperty("MonsterGroupsFollowingCategory")!.GetValue(active)!).Clear();
            ((IList)configType.GetProperty("MonsterGroupsWithManualSelection")!.GetValue(active)!).Add("monster:a");
            ((IDictionary)configType.GetProperty("ActiveCardSkinPresets")!.GetValue(active)!).Clear();
            ((IDictionary)configType.GetProperty("ActiveMonsterSkinPresets")!.GetValue(active)!).Clear();
            AccessTools.Method(service, "SaveCharacterSkinBundleRunPresets").Invoke(null, null);
            using (var persisted = JsonDocument.Parse(File.ReadAllText(path)))
            {
                Require(persisted.RootElement.GetProperty("Cards")[0].GetProperty("Selections").GetProperty("cards:silent").GetString() == "__base__" &&
                        persisted.RootElement.GetProperty("Monsters")[0].GetProperty("Selections").GetProperty("monster:a").GetString() == "__base__",
                    "局内手动改变应写入本局恢复记录，不改写皮肤包或全局预设。");
                Require(persisted.RootElement.GetProperty("Cards")[0].GetProperty("Name").GetString() == string.Empty &&
                        persisted.RootElement.GetProperty("Monsters")[0].GetProperty("Name").GetString() == string.Empty,
                    "已手动离开某预设的本局选择不能在读档后冒充仍使用该预设。");
            }
            AccessTools.Method(service, "RestoreCharacterSkinBundleAfterRun").Invoke(null, null);
            Require(JsonSerializer.Serialize(configProperty.GetValue(null), configType) == before,
                "保存过局内改动以后退出也必须恢复全局配置。");
            _failMount = true;
            Require(!(bool)resume.Invoke(null, [loaded])! &&
                    JsonSerializer.Serialize(configProperty.GetValue(null), configType) == before,
                "恢复时资源加载失败必须回滚全局配置，不能留下半个包预设。");
            _failMount = false;
            VerifyModPriorityRun(assembly, service, configType, configProperty, path);
            AccessTools.Method(store, "Save").Invoke(null, [path, State("run-B")]);
            Require(AccessTools.Method(store, "LoadMatching").Invoke(null, [path, "run-A"]) == null,
                "新开局覆盖该存档槽之后，旧局记录不能继续生效。");
        }
        finally
        {
            _failMount = false;
            harmony.UnpatchAll(harmony.Id);
            configProperty.SetValue(null, previousConfig);
            catalogProperty.SetValue(null, previousCatalog);
            foreach (var field in previousFields) AccessTools.Field(service, field.Key).SetValue(null, field.Value);
            Directory.Delete(_directory, recursive: true);
        }
        VerifyGameHooks(assembly);
        Console.WriteLine("Bundle run resume passed: persistent run identity, card/monster restoration, global isolation and repeated continue.");
    }

    private static void VerifyModPriorityRun(Assembly assembly, Type service, Type configType,
        PropertyInfo configProperty, string path)
    {
        var config = AccessTools.Method(configType, "Deserialize").Invoke(null, ["""
            {"Selections":{"silent":"character:keep","merchant":"merchant:keep","monster:a":"__base__","cards:silent":"__base__"},
             "MonsterSkinCategoryGroups":{"act:one":["monster:a"]},
             "CardPriorityDefaultsVersion":1,"MonsterPriorityDefaultsVersion":2,
             "CharacterSkinBundles":[{"Id":"modes","Name":"模式包","CharacterGroupId":"silent",
                 "CardMode":1,"MonsterMode":1,"CardModPriority":[{"OptionId":"skin:a","Enabled":true}],
                 "MonsterModPriority":[{"OptionId":"skin:a","Enabled":true}]}],
             "ActiveCharacterSkinBundles":{"silent":"模式包"}}
            """])!;
        configProperty.SetValue(null, config);
        var before = JsonSerializer.Serialize(config, configType);
        object?[] arguments = ["silent", null];
        _failMount = true;
        Require(!(bool)AccessTools.Method(service, "ApplySelectedCharacterSkinBundleForRun").Invoke(null, arguments)! &&
                JsonSerializer.Serialize(configProperty.GetValue(null), configType) == before &&
                AccessTools.Field(service, "_characterSkinBundleRunSnapshot").GetValue(null) == null,
            "Mod 模式开局挂载失败必须回滚，不能留下本局半应用状态。");
        _failMount = false;
        Require((bool)AccessTools.Method(service, "ApplySelectedCharacterSkinBundleForRun").Invoke(null, arguments)!,
            "Mod 优先级应能通过真实开局事务应用：" + AccessTools.Property(service, "LastError").GetValue(null));
        var active = configProperty.GetValue(null)!;
        var selections = (IDictionary)configType.GetProperty("Selections")!.GetValue(active)!;
        Require((string?)selections["cards:silent"] == "skin:a" && (string?)selections["monster:a"] == "skin:a",
            "开局必须应用所选 Mod 模式，不能仍读取未启用的包预设引用。");
        Require((string?)selections["silent"] == "character:keep" && (string?)selections["merchant"] == "merchant:keep",
            "Mod 模式不能重新挂载角色、改变商人或混入角色资源。");
        AccessTools.Field(service, "_characterSkinBundleRunSavePath").SetValue(null, path);
        AccessTools.Method(service, "SaveCharacterSkinBundleRunPresets").Invoke(null, null);
        var stateType = assembly.GetType("STS2SkinChanger.Core.CharacterSkinBundleRunState", true)!;
        var saved = JsonSerializer.Deserialize(File.ReadAllText(path), stateType)!;
        Require(((IList)stateType.GetProperty("Cards")!.GetValue(saved)!).Count == 1 &&
                ((IList)stateType.GetProperty("Monsters")!.GetValue(saved)!).Count == 1,
            "没有手工预设引用的 Mod 模式，也必须保存所有实际应用分类的快照。");
        AccessTools.Method(service, "RestoreCharacterSkinBundleAfterRun").Invoke(null, null);
        Require(JsonSerializer.Serialize(configProperty.GetValue(null), configType) == before,
            "Mod 模式离开本局应完整恢复全局配置。");
        // Continue consumes the captured settings, not the bundle's now-edited mode or order.
        ((IList)configType.GetProperty("CharacterSkinBundles")!.GetValue(config)!).Clear();
        Require((bool)AccessTools.Method(service, "ResumeCharacterSkinBundleForRun").Invoke(null, [saved])!,
            "编辑或删除皮肤包后仍必须能恢复原局的实际 Mod 优先级。");
        var resumed = (IDictionary)configType.GetProperty("Selections")!.GetValue(configProperty.GetValue(null))!;
        Require((string?)resumed["cards:silent"] == "skin:a" && (string?)resumed["monster:a"] == "skin:a",
            "继续游戏不能丢掉 Mod 模式的卡牌和怪物选择。");
        AccessTools.Method(service, "RestoreCharacterSkinBundleAfterRun").Invoke(null, null);
    }

    private static void VerifyGameHooks(Assembly assembly)
    {
        var harmony = new Harmony("sc-tests.bundle-run-hooks");
        try
        {
            foreach (var name in new[] { "CharacterSkinBundleNewRunBindingPatch", "CharacterSkinBundleSavedRunPatch", "CharacterSkinBundleRunSavePatch" })
            {
                var patch = assembly.GetType("STS2SkinChanger.Core." + name, true)!;
                Require(harmony.CreateClassProcessor(patch).Patch().Count > 0,
                    "新开局、单人和多人读档、存档挂钩必须能绑定当前版本的真实游戏方法：" + name);
            }
            foreach (var name in new[] { "SetUpSavedSingleplayer", "SetUpSavedMultiplayer" })
                Require(Harmony.GetPatchInfo(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Runs.RunManager), name))!
                    .Prefixes.Any(p => p.owner == harmony.Id), "两个读档入口都必须在创建场景之前恢复包配置。");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }

    private static object CreateCatalog(Assembly assembly, Type catalogType)
    {
        var catalog = RuntimeHelpers.GetUninitializedObject(catalogType);
        foreach (var (groupName, optionName, field, id) in new[] {
                     ("CardSkinGroup", "CardSkinOption", "_cardGroups", "silent"),
                     ("SkinGroup", "SkinOption", "_groups", "monster:a") })
        {
            var groupType = assembly.GetType("STS2SkinChanger.Catalog." + groupName, true)!;
            var optionType = assembly.GetType("STS2SkinChanger.Catalog." + optionName, true)!;
            var group = Activator.CreateInstance(groupType, [id, id])!;
            var constructor = optionType.GetConstructors().Single(c => c.GetParameters().Length > 2);
            var args = constructor.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue :
                p.ParameterType.IsGenericType ? Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(p.ParameterType.GenericTypeArguments)) : null).ToArray();
            args[0] = "skin:a"; args[1] = "skin:a";
            ((IList)groupType.GetProperty("Options")!.GetValue(group)!).Add(constructor.Invoke(args));
            var groups = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(groupType))!;
            groups.Add(group);
            AccessTools.Field(catalogType, field).SetValue(catalog, groups);
        }
        var identities = AccessTools.Field(catalogType, "_providerInstanceIdentities");
        identities.SetValue(catalog, Array.CreateInstance(identities.FieldType.GenericTypeArguments[0], 0));
        AccessTools.Field(catalogType, "_fullRuntimeProviders").SetValue(catalog, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        AccessTools.Field(catalogType, "_fullRuntimeProviderGroups").SetValue(catalog,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));
        return catalog;
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
