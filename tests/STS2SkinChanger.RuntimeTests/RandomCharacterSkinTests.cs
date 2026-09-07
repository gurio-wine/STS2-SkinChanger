using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HarmonyLib;
using STS2SkinChanger;

internal static class RandomCharacterSkinTests
{
    private static readonly Assembly Mod = typeof(Entry).Assembly;
    private static string _configPath = string.Empty;
    private static bool ConfigPath(ref string __result) { __result = _configPath; return false; }
    private static int _mountCalls;
    private static bool _failMount;
    private static bool MountBoundary()
    {
        _mountCalls++;
        if (_failMount) throw new IOException("Test vanilla mount failure");
        return false;
    }
    private static bool SkipLog(object[] __args)
    {
        if (!_failMount) Console.WriteLine("Random lobby transaction: " + __args[0]);
        return false;
    }

    public static void Run()
    {
        var service = Mod.GetType("STS2SkinChanger.Core.SkinService", true)!;
        var property = AccessTools.Property(service, "Config");
        var previous = property.GetValue(null);
        var config = AccessTools.Method(property.PropertyType, "Deserialize").Invoke(null,
            ["""{"Selections":{"silent":"skin:a","regent":"skin:b"},"RandomCharacterSkinGroups":["SILENT","silent"]}"""])!;
        try
        {
            property.SetValue(null, config);
            Require((string?)AccessTools.Method(service, "GetCharacterSelectionOptionId").Invoke(null, ["silent"]) == "__random_character_skin__",
                "保存的随机意图必须显示为随机，而非上一套皮肤。");
            Require((string?)AccessTools.Method(service, "GetVisualSelection").Invoke(null, ["silent"]) == "skin:a" &&
                    (string?)AccessTools.Method(service, "GetCharacterSelectionOptionId").Invoke(null, ["regent"]) == "skin:b",
                "读取随机意图不能重抽或改变其它角色，资源层仍只读取已提交的真实皮肤。");
            var clone = AccessTools.Method(property.PropertyType, "CloneForBundleTransaction").Invoke(config, null)!;
            ((IList)clone.GetType().GetProperty("RandomCharacterSkinGroups")!.GetValue(clone)!).Clear();
            Require(((IList)config.GetType().GetProperty("RandomCharacterSkinGroups")!.GetValue(config)!).Count == 1,
                "随机设置应忽略大小写去重，并在事务中独立复制。");
        }
        finally { property.SetValue(null, previous); }

        var policy = Mod.GetType("STS2SkinChanger.Core.RandomCharacterSkinPolicy", true)!;
        var draw = AccessTools.Method(policy, "Draw");
        var bundlePolicy = Mod.GetType("STS2SkinChanger.Core.CharacterSkinBundlePolicy", true)!;
        var bundle = (string)AccessTools.Method(bundlePolicy, "CreateSelectionOptionId").Invoke(null, ["我的包"])!;
        var pool = new[] { "skin:a", "__base__", "skin:a", "__random_character_skin__", "", bundle, "merge:a" };
        var choices = new List<string>();
        for (var index = 0; index < 4; index++)
        {
            var selected = index;
            choices.Add((string)draw.Invoke(null, [pool, (Func<int, int>)(count =>
            {
                Require(count == 4, "随机池只保留不重复的有效选项，不包含随机本身。");
                return selected;
            })])!);
        }
        Require(choices.SequenceEqual(new[] { "skin:a", "__base__", bundle, "merge:a" }),
            "原皮、普通皮肤、合并皮肤和皮肤包都应可被抽取。");
        Require((string?)draw.Invoke(null, [Array.Empty<string>(), (Func<int, int>)(_ => throw new Exception("空池不应调用 RNG"))]) == "__base__",
            "没有可用皮肤时必须安全回退原皮。");
        CheckRunRecord();
        CheckSavedPreferenceAndCleanup(service);
        CheckReturnToLobby(service);
        CheckUiAndRunBoundaries(service);
        Console.WriteLine("Random character skins passed: deferred intent, visible candidates, independent RNG and saved-run choice.");
    }

    private static void CheckReturnToLobby(Type service)
    {
        var prepare = AccessTools.Method(service, "RestoreRandomCharacterSkinsForLobby");
        Require(prepare != null, "重新进入选角缺少随机皮肤的原皮恢复流程，上局的实际来源会泄漏到大厅。");
        var configProperty = AccessTools.Property(service, "Config");
        var catalogProperty = AccessTools.Property(service, "Catalog");
        var configType = configProperty.PropertyType;
        var previousConfig = configProperty.GetValue(null);
        var previousCatalog = catalogProperty.GetValue(null);
        var previousError = AccessTools.Property(service, "LastError").GetValue(null);
        var fields = new[] { "_characterSkinBundleRunSnapshot", "_characterSkinBundleRunState", "_characterSkinBundleRunSavePath" }
            .ToDictionary(name => name, name => AccessTools.Field(service, name).GetValue(null));
        var directory = Path.Combine(Path.GetTempPath(), "sc-random-lobby-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _configPath = Path.Combine(directory, "config.json");
        var runPath = Path.Combine(directory, "run.json");
        var stateType = Mod.GetType("STS2SkinChanger.Core.CharacterSkinBundleRunState", true)!;
        var state = JsonSerializer.Deserialize("""{"RunIdentity":"run-A","CharacterGroupId":"silent","RandomCharacterOptionId":"skin:a"}""", stateType)!;
        var harmony = new Harmony("sc-tests.random-lobby-return");
        try
        {
            harmony.Patch(AccessTools.PropertyGetter(service, "ConfigPath"), prefix: new HarmonyMethod(typeof(RandomCharacterSkinTests), nameof(ConfigPath)));
            // Only the Godot resource-mount boundary is replaced. Selection transactions,
            // provider/companion ownership, config writes and run records stay real.
            harmony.Patch(AccessTools.Method(service, "MountOverlay"), prefix: new HarmonyMethod(typeof(RandomCharacterSkinTests), nameof(MountBoundary)));
            foreach (var method in Mod.GetType("STS2SkinChanger.Core.ModLog", true)!.GetMethods().Where(m => m.Name is "Info" or "Warn" or "Error"))
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(RandomCharacterSkinTests), nameof(SkipLog)));
            var config = AccessTools.Method(configType, "Deserialize").Invoke(null, ["""
                {"Selections":{"silent":"skin:a","regent":"skin:a","monster:a":"skin:a"},
                 "RandomCharacterSkinGroups":["silent","missing","monster:a"],"ActiveCharacterSkinBundles":{"silent":"CZN"}}
                """])!;
            configProperty.SetValue(null, config);
            catalogProperty.SetValue(null, CreateLobbyCatalog(catalogProperty.PropertyType));
            foreach (var field in fields.Keys) AccessTools.Field(service, field).SetValue(null, null);
            AccessTools.Field(service, "_characterSkinBundleRunState").SetValue(null, state);
            AccessTools.Field(service, "_characterSkinBundleRunSavePath").SetValue(null, runPath);
            _mountCalls = 0;
            _failMount = false;
            string Actual(string group) => (string)AccessTools.Method(service, "GetVisualSelection").Invoke(null, [group])!;
            prepare!.Invoke(null, null);
            Require(Actual("silent") == "skin:a" && _mountCalls == 0, "仍在对局时不能因为残留选角节点而重置随机结果。");
            AccessTools.Method(service, "RestoreCharacterSkinBundleAfterRun").Invoke(null, null);
            var beforeLobby = File.ReadAllText(runPath);
            prepare.Invoke(null, null);
            Require(Actual("silent") == "__base__" && Actual("regent") == "skin:a" && Actual("monster:a") == "skin:a",
                $"返回选角只恢复选择随机的角色：silent={Actual("silent")}，regent={Actual("regent")}，monster={Actual("monster:a")}；错误={AccessTools.Property(service, "LastError").GetValue(null)}");
            Require((string?)AccessTools.Method(service, "GetCharacterSelectionOptionId").Invoke(null, ["silent"]) == "__random_character_skin__" &&
                    !((IDictionary)configType.GetProperty("ActiveCharacterSkinBundles")!.GetValue(configProperty.GetValue(null))!).Contains("silent"),
                "恢复原皮必须保留随机意图，并清除上局随机抽到的皮肤包标记。");
            Require(_mountCalls == 1 && File.ReadAllText(runPath) == beforeLobby,
                "必须实际热切换原皮，不能只改配置；也不能把原皮写回旧局的随机结果。");
            prepare.Invoke(null, null);
            Require(_mountCalls == 1, "重复打开已经是原皮的随机选项不能重复挂载资源。");
            var store = Mod.GetType("STS2SkinChanger.Core.CharacterSkinBundleRunStore", true)!;
            var saved = AccessTools.Method(store, "LoadMatching").Invoke(null, [runPath, "run-A"])!;
            AccessTools.Method(service, "ResumeRandomCharacterSkin").Invoke(null, [saved]);
            Require(Actual("silent") == "skin:a", "大厅恢复原皮之后，继续旧局仍应恢复该局的确切皮肤，不能重抽。");
            // A restart can load the previous run's actual selection from global config too.
            var restarted = AccessTools.Method(configType, "Load").Invoke(null, [_configPath])!;
            configProperty.SetValue(null, restarted);
            prepare.Invoke(null, null);
            Require(Actual("silent") == "__base__" && File.ReadAllText(runPath) == beforeLobby,
                "重新启动后进入新选角也应恢复原皮，保留旧局记录。");
            ((IDictionary)configType.GetProperty("Selections")!.GetValue(configProperty.GetValue(null))!)["silent"] = "skin:a";
            _failMount = true;
            prepare.Invoke(null, null);
            Require(Actual("silent") == "skin:a" && File.ReadAllText(runPath) == beforeLobby,
                "恢复失败应保留原事务状态，不能清空皮肤或破坏旧局记录。");
        }
        finally
        {
            _failMount = false;
            harmony.UnpatchAll(harmony.Id);
            configProperty.SetValue(null, previousConfig);
            catalogProperty.SetValue(null, previousCatalog);
            AccessTools.Property(service, "LastError").SetValue(null, previousError);
            foreach (var field in fields) AccessTools.Field(service, field.Key).SetValue(null, field.Value);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static object CreateLobbyCatalog(Type type)
    {
        var catalog = RuntimeHelpers.GetUninitializedObject(type);
        var groupType = Mod.GetType("STS2SkinChanger.Catalog.SkinGroup", true)!;
        var optionType = Mod.GetType("STS2SkinChanger.Catalog.SkinOption", true)!;
        var groups = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(groupType))!;
        foreach (var id in new[] { "silent", "regent", "monster:a" })
        {
            var group = Activator.CreateInstance(groupType, [id, id])!;
            var constructor = optionType.GetConstructors().Single(c => c.GetParameters().Length > 2);
            var args = constructor.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue :
                p.ParameterType.IsGenericType ? Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(p.ParameterType.GenericTypeArguments)) : null).ToArray();
            args[0] = "skin:a"; args[1] = "Test skin";
            args[3] = id != "monster:a"; // Known character groups supplied by a DLL skin.
            ((IList)groupType.GetProperty("Options")!.GetValue(group)!).Add(constructor.Invoke(args));
            groups.Add(group);
        }
        AccessTools.Field(type, "_groups").SetValue(catalog, groups);
        AccessTools.Field(type, "_characterAppearanceGroupIds").SetValue(catalog, new HashSet<string>(["silent", "regent"], StringComparer.OrdinalIgnoreCase));
        AccessTools.Field(type, "_fullRuntimeProviders").SetValue(catalog, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        AccessTools.Field(type, "_fullRuntimeProviderGroups").SetValue(catalog, new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));
        var identities = AccessTools.Field(type, "_providerInstanceIdentities");
        identities.SetValue(catalog, Array.CreateInstance(identities.FieldType.GenericTypeArguments[0], 0));
        return catalog;
    }

    private static void CheckSavedPreferenceAndCleanup(Type service)
    {
        var directory = Path.Combine(Path.GetTempPath(), "sc-random-skin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _configPath = Path.Combine(directory, "config.json");
        var property = AccessTools.Property(service, "Config");
        var configType = property.PropertyType;
        var previous = property.GetValue(null);
        var fields = new[] { "_characterSkinBundleRunSnapshot", "_characterSkinBundleRunState", "_characterSkinBundleRunSavePath" }
            .ToDictionary(name => name, name => AccessTools.Field(service, name).GetValue(null));
        var harmony = new Harmony("sc-tests.random-character-skin");
        try
        {
            harmony.Patch(AccessTools.PropertyGetter(service, "ConfigPath"),
                prefix: new HarmonyMethod(typeof(RandomCharacterSkinTests), nameof(ConfigPath)));
            var config = AccessTools.Method(configType, "Deserialize").Invoke(null, ["""
                {"Selections":{"silent":"skin:a","regent":"skin:other"},"ActiveCharacterSkinBundles":{"silent":"包"}}
                """])!;
            property.SetValue(null, config);
            foreach (var field in fields.Keys) AccessTools.Field(service, field).SetValue(null, null);
            var setRandom = AccessTools.Method(service, "SetRandomCharacterSkinEnabled");
            Require((bool)setRandom.Invoke(null, ["silent", true])!, "选择随机必须可以持久化。");
            var persisted = AccessTools.Method(configType, "Load").Invoke(null, [_configPath])!;
            var selections = (IDictionary)configType.GetProperty("Selections")!.GetValue(persisted)!;
            Require((string?)selections["silent"] == "skin:a" && (string?)selections["regent"] == "skin:other" &&
                    !((IDictionary)configType.GetProperty("ActiveCharacterSkinBundles")!.GetValue(persisted)!).Contains("silent"),
                "意图存储不直接挂载资源，原皮由正常选择事务负责；不能残留旧皮肤包指令。");
            property.SetValue(null, persisted);
            Require((string?)AccessTools.Method(service, "GetCharacterSelectionOptionId").Invoke(null, ["silent"]) == "__random_character_skin__",
                "重启后仍应显示玩家选中的随机选项。");
            Require((bool)setRandom.Invoke(null, ["silent", false])! &&
                    (string?)AccessTools.Method(service, "GetCharacterSelectionOptionId").Invoke(null, ["silent"]) == "skin:a",
                "明确选择普通皮肤后不能继续保留随机指令。");

            // Exercise the actual sidecar writer and cleanup, with no bundle snapshot at all.
            var stateType = Mod.GetType("STS2SkinChanger.Core.CharacterSkinBundleRunState", true)!;
            var state = JsonSerializer.Deserialize("""{"RunIdentity":"run-A","CharacterGroupId":"silent","RandomCharacterOptionId":"skin:b"}""", stateType)!;
            var runPath = Path.Combine(directory, "run.json");
            AccessTools.Field(service, "_characterSkinBundleRunState").SetValue(null, state);
            AccessTools.Field(service, "_characterSkinBundleRunSavePath").SetValue(null, runPath);
            var liveSelections = (IDictionary)configType.GetProperty("Selections")!.GetValue(property.GetValue(null))!;
            liveSelections["silent"] = "skin:in-run";
            AccessTools.Method(service, "RestoreCharacterSkinBundleAfterRun").Invoke(null, null);
            var store = Mod.GetType("STS2SkinChanger.Core.CharacterSkinBundleRunStore", true)!;
            var saved = AccessTools.Method(store, "LoadMatching").Invoke(null, [runPath, "run-A"]);
            Require(saved != null && (string?)stateType.GetProperty("RandomCharacterOptionId")!.GetValue(saved) == "skin:in-run",
                "退出必须保存本局最终的实际来源，即使没有皮肤包，也不能丢掉局内手动换肤。");
            Require(AccessTools.Field(service, "_characterSkinBundleRunState").GetValue(null) == null &&
                    AccessTools.Field(service, "_characterSkinBundleRunSavePath").GetValue(null) == null &&
                    AccessTools.Method(store, "LoadMatching").Invoke(null, [runPath, "other-run"]) == null,
                "退出应清理内存标记但保留读档记录；另一局不能借用此结果。");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            property.SetValue(null, previous);
            foreach (var field in fields) AccessTools.Field(service, field.Key).SetValue(null, field.Value);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void CheckUiAndRunBoundaries(Type service)
    {
        string[] Calls(Type type, string name) => PatchProcessor.GetOriginalInstructions(AccessTools.Method(type, name))
            .Select(i => i.operand).OfType<MethodInfo>().Select(m => m.Name).ToArray();
        var controls = Mod.GetType("STS2SkinChanger.Ui.ContextualSkinControls", true)!;
        var accent = AccessTools.Method(controls, "IsAccentedCharacterOption");
        Require((bool)accent.Invoke(null, ["__random_character_skin__"])! && !(bool)accent.Invoke(null, ["skin:a"])!,
            "随机文字必须走主题强调色，普通皮肤不能同时变成强调色。");
        var localization = Mod.GetType("STS2SkinChanger.Core.ModLocalization", true)!;
        var texts = (IDictionary)AccessTools.Field(localization, "RandomCharacterSkinTexts").GetValue(null)!;
        var languages = (IEnumerable<string>)AccessTools.Property(localization, "SupportedLanguages").GetValue(null)!;
        Require(languages.All(language => texts.Contains(language) && !string.IsNullOrWhiteSpace((string?)texts[language])),
            "随机皮肤文字必须覆盖全部工坊语言。");
        foreach (var name in new[] { "SingleplayerEmbarkSkinSelectorPatch", "MultiplayerEmbarkSkinSelectorPatch" })
        {
            var calls = Calls(Mod.GetType("STS2SkinChanger.Ui." + name, true)!, "Prefix");
            Require(Array.IndexOf(calls, "IsRandomCharacterSkinEnabled") < Array.IndexOf(calls, "ApplySelectedCharacterSkinBundleForRun"),
                "随机开局不能在淡出之前先应用上次抽到的皮肤包。");
        }
        Require(Calls(service, "BindNewCharacterSkinBundleRun").Contains("ApplyRandomCharacterSkinForNewRun") &&
                !Calls(service, "LoadCharacterSkinBundleRun").Contains("ApplyRandomCharacterSkinForNewRun") &&
                Calls(service, "LoadCharacterSkinBundleRun").Contains("ResumeRandomCharacterSkin") &&
                Calls(service, "ResumeRandomCharacterSkin").Contains("ApplySelection"),
            "只有新开局抽取；读档必须用通用换肤流程恢复存储结果，不能重抽。");
        var apply = PatchProcessor.GetOriginalInstructions(AccessTools.Method(controls, "ApplyDropdownSelection"));
        var firstLoad = apply.FindIndex(i => i.operand is MethodInfo { Name: "BeginCharacterDropdownSelection" });
        Require(firstLoad >= 3 && apply[firstLoad - 3].opcode == OpCodes.Ldstr &&
                (string?)apply[firstLoad - 3].operand == "__base__" &&
                apply[firstLoad - 2].opcode == OpCodes.Ldc_I4_0 && apply[firstLoad - 1].opcode == OpCodes.Ldc_I4_1,
            "随机分支必须调用普通切肤流程加载原皮，同时携带随机意图，不能停留在之前的皮肤。");
        var commit = Calls(controls, "ApplyDropdownSelectionNow");
        Require(commit.Contains("SetRandomCharacterSkinEnabled") &&
                Array.IndexOf(commit, "ApplySelection") < Array.IndexOf(commit, "SetRandomCharacterSkinEnabled") &&
                commit.Contains("OnLocalCharacterSelectionChanged"),
            "先成功切回原皮再保存随机指令，并通过本机换肤流程同步实际原皮和头像。");
        var lobbyPatch = Mod.GetType("STS2SkinChanger.Ui.RandomCharacterLobbySkinPatch", true)!;
        var harmony = new Harmony("sc-tests.random-lobby-hook");
        try
        {
            Require(harmony.CreateClassProcessor(lobbyPatch).Patch().Count == 1,
                "返回选角恢复必须能绑定当前游戏版本的真实入口。");
            var entry = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectScreen), "OnSubmenuOpened");
            Require(Harmony.GetPatchInfo(entry)!.Prefixes.Any(p => p.owner == harmony.Id) &&
                    Calls(lobbyPatch, "Prefix").Contains("RestoreRandomCharacterSkinsForLobby"),
                "必须在选角原生预览创建之前恢复原皮，不是预览已经显示后的补丁。");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }

    private static void CheckRunRecord()
    {
        var type = Mod.GetType("STS2SkinChanger.Core.CharacterSkinBundleRunState", true)!;
        var state = JsonSerializer.Deserialize("""{"RunIdentity":"one-run","CharacterGroupId":"silent","RandomCharacterOptionId":"skin:b"}""", type)!;
        Require((string?)type.GetProperty("RandomCharacterOptionId")?.GetValue(state) == "skin:b",
            "随机结果必须跟随对局记录保存，不能读档重新抽取。");
        var roundtrip = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, type), type)!;
        Require((string?)type.GetProperty("RandomCharacterOptionId")!.GetValue(roundtrip) == "skin:b",
            "再次启动继续游戏必须恢复确切来源。");
        var old = JsonSerializer.Deserialize("""{"CharacterGroupId":"silent","BundleName":"旧包"}""", type)!;
        Require(type.GetProperty("RandomCharacterOptionId")!.GetValue(old) == null,
            "旧皮肤包存档不能被推断为随机开局。");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
