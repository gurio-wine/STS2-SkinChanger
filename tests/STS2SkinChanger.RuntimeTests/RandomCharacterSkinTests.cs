using System.Collections;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using STS2SkinChanger;

internal static class RandomCharacterSkinTests
{
    private static readonly Assembly Mod = typeof(Entry).Assembly;
    private static string _configPath = string.Empty;
    private static bool ConfigPath(ref string __result) { __result = _configPath; return false; }

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
                "选中随机不能改变当前预览或其它角色，资源层只能读取真实皮肤。");
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
        CheckUiAndRunBoundaries(service);
        Console.WriteLine("Random character skins passed: deferred intent, visible candidates, independent RNG and saved-run choice.");
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
                "随机只替换选角指令，不改变实际资源，也不能残留即将开局的旧皮肤包指令。");
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
        var apply = Calls(controls, "ApplyDropdownSelection");
        Require(apply.Contains("SetRandomCharacterSkinEnabled") &&
                Array.IndexOf(apply, "SetRandomCharacterSkinEnabled") < Array.IndexOf(apply, "BeginCharacterDropdownSelection"),
            "随机选项必须先独立处理，不能交给立即预加载流程。");
        Require(!apply.Contains("QueueRefreshControls"),
            "选择随机时也不能重建原管理器模型预览，只刷新选项名称。");
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
