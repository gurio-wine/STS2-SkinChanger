using System.Collections;
using System.Reflection;
using System.Text.Json;
using STS2SkinChanger;

internal static class CharacterSkinBundleContractTests
{
    public static void Run()
    {
        var assembly = typeof(Entry).Assembly;
        var configType = assembly.GetType("STS2SkinChanger.Core.SkinConfig", true)!;
        var deserialize = configType.GetMethod("Deserialize", BindingFlags.NonPublic | BindingFlags.Static)!;
        var original = deserialize.Invoke(null, ["""
            {"Selections":{"SILENT":"skin:one","cards:silent":"card:one","monster:one":"skin:old"},
             "CharacterSkinBundles":[{"Name":"综合包","CharacterGroupId":"SILENT","CharacterOptionId":"composition:one",
               "CardPresetNames":{"silent":"卡牌包"},"MonsterPresetNames":{"act:one":"地区包"}}],
             "ActiveCharacterSkinBundles":{"SILENT":"综合包"},
             "CharacterSkinBundleX":0.25,"CharacterSkinBundleY":0.4,
             "CharacterSkinSelectorX":0.7,"CharacterSkinSelectorY":0.8,
             "CharacterSkinMergeX":0.9,"CharacterSkinMergeY":0.6,
             "CardPriorityDefaultsVersion":1,"MonsterPriorityDefaultsVersion":2,
             "CardSkinPriorities":{"silent":[{"OptionId":"card:one","Enabled":true}]},
             "MonsterSkinPriorities":{"act:one":[{"OptionId":"skin:old","Enabled":true}]},
             "VisualProviderPriority":["skin:one"],
             "ActiveCardSkinPresets":{"silent":"卡牌包"},
             "ActiveMonsterSkinPresets":{"act:one":"地区包"},
             "MonsterSkinCategoryGroups":{"act:one":["monster:one"]},
             "MonsterGroupsFollowingCategory":["monster:one"],"MonsterGroupsWithManualSelection":["monster:two"]}
            """])!;
        var before = JsonSerializer.Serialize(original, configType);
        var clone = configType.GetMethod("CloneForBundleTransaction", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(original, null)!;
        foreach (var property in new[] { "Selections", "ActiveCharacterSkinBundles", "ActiveCardSkinPresets", "ActiveMonsterSkinPresets" })
        {
            ((IDictionary)configType.GetProperty(property)!.GetValue(clone)!)["SILENT"] = "changed";
        }
        foreach (var property in new[] { "CardSkinPriorities", "MonsterSkinPriorities", "MonsterSkinCategoryGroups" })
        {
            var dictionary = (IDictionary)configType.GetProperty(property)!.GetValue(clone)!;
            ((IList)dictionary.Values.Cast<object>().First()).Clear();
        }
        foreach (var property in new[] { "VisualProviderPriority", "MonsterGroupsFollowingCategory", "MonsterGroupsWithManualSelection" })
        {
            ((IList)configType.GetProperty(property)!.GetValue(clone)!).Clear();
        }
        var clonedBundles = (IList)configType.GetProperty("CharacterSkinBundles")!.GetValue(clone)!;
        ((IDictionary)clonedBundles[0]!.GetType().GetProperty("CardPresetNames")!.GetValue(clonedBundles[0])!).Clear();
        Require(JsonSerializer.Serialize(original, configType) == before,
            "皮肤包的暂存字典、列表和引用不能与原配置共享可变对象。");

        var path = Path.Combine(Path.GetTempPath(), "skin-changer-bundle-contract-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            configType.GetMethod("Save")!.Invoke(original, [path]);
            var loaded = configType.GetMethod("Load")!.Invoke(null, [path])!;
            Require(JsonSerializer.Serialize(loaded, configType) == before,
                "保存重进必须保留皮肤包、合并皮肤 ID、各分类引用和三个独立按钮位置。");
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bak");
        }
        var controls = assembly.GetType("STS2SkinChanger.Ui.CharacterSkinBundleControls", true)!;
        Require(controls.GetMethod("ShowForCharacter", BindingFlags.Static | BindingFlags.NonPublic) != null &&
                controls.GetMethod("Hide", BindingFlags.Static | BindingFlags.NonPublic) != null,
            "皮肤包必须按当前角色显示，并在退出选角时一起隐藏。");

        var service = assembly.GetType("STS2SkinChanger.Core.SkinService", true)!;
        Require(service.GetMethod("SelectCharacterSkinBundle", BindingFlags.Static | BindingFlags.Public) != null &&
                service.GetMethod("ClearSelectedCharacterSkinBundle", BindingFlags.Static | BindingFlags.Public) != null &&
                service.GetMethod("GetCharacterSkinBundleCharacterOption", BindingFlags.Static | BindingFlags.Public) != null &&
                service.GetMethod("ApplySelectedCharacterSkinBundleForRun", BindingFlags.Static | BindingFlags.Public) != null,
            "皮肤包必须能立即解析角色皮肤，同时把卡牌和怪物预设留到开始对局时应用。");
        Require(controls.GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic) == null,
            "管理皮肤包界面不能保留立即应用入口。");

        var contextualControls = assembly.GetType("STS2SkinChanger.Ui.ContextualSkinControls", true)!;
        var localCharacterReader = contextualControls.GetMethod(
            "GetLocalLobbyCharacter", BindingFlags.Static | BindingFlags.NonPublic);
        Require(localCharacterReader != null,
            "皮肤包开局必须兼容正式版 LobbyPlayer 和测试版 StartRunLobbyPlayer，不能绑定某一版 LocalPlayer 返回类型。");
        var lobbyType = typeof(MegaCrit.Sts2.Core.Multiplayer.Game.Lobby.StartRunLobby);
        // Only field identity and NetId are needed; constructors may access native Godot.
        var lobby = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(lobbyType);
        var netService = (MegaCrit.Sts2.Core.Multiplayer.NetSingleplayerGameService)
            System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                typeof(MegaCrit.Sts2.Core.Multiplayer.NetSingleplayerGameService));
        HarmonyLib.AccessTools.Field(lobbyType, "<NetService>k__BackingField").SetValue(lobby, netService);
        var playersType = lobbyType.GetProperty("Players")!.PropertyType;
        var players = (IList)Activator.CreateInstance(playersType)!;
        var playerType = playersType.GetGenericArguments()[0];
        var player = Activator.CreateInstance(playerType)!;
        // Only identity is needed here. Constructing a model boots ModelDb/Godot on beta,
        // which is unavailable in this headless contract runner.
        var character = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
            typeof(MegaCrit.Sts2.Core.Models.Characters.Ironclad));
        playerType.GetField("id")!.SetValue(player, netService.NetId);
        playerType.GetField("character")!.SetValue(player, character);
        players.Add(player);
        HarmonyLib.AccessTools.Field(lobbyType, "<Players>k__BackingField").SetValue(lobby, players);
        Require(ReferenceEquals(localCharacterReader!.Invoke(null, [lobby]), character),
            "开始对局读取的必须是当前游戏版本大厅中的本机角色。");

        var populate = contextualControls.GetMethod("Populate", BindingFlags.Static | BindingFlags.NonPublic)!;
        var populateCalls = HarmonyLib.PatchProcessor.GetOriginalInstructions(populate);
        Require(populateCalls.Any(instruction => instruction.operand is MethodInfo called &&
                    called.Name == "GetCharacterSkinBundles"),
            "选角皮肤列表必须把当前角色保存的皮肤包置于普通皮肤之前。");
        Require(populateCalls.Any(instruction => instruction.operand is MethodInfo called &&
                    called.Name == "ConfigureCharacterBundlePopupList"),
            "选角皮肤列表中的 [P] 皮肤包必须使用逐项黄色文字，不能退化为黄色标记。");

        var applyDropdown = contextualControls.GetMethod(
            "ApplyDropdownSelection", BindingFlags.Static | BindingFlags.NonPublic)!;
        var applyDropdownCalls = HarmonyLib.PatchProcessor.GetOriginalInstructions(applyDropdown);
        Require(applyDropdownCalls.Any(instruction => instruction.operand is MethodInfo called &&
                    called.Name == "GetCharacterSkinBundleCharacterOption") &&
                applyDropdownCalls.Any(instruction => instruction.operand is MethodInfo called &&
                    called.Name == "BeginCharacterDropdownSelection"),
            "选中皮肤包时必须立即复用普通角色皮肤热切换流程。");

        var applyForRun = service.GetMethod(
            "ApplySelectedCharacterSkinBundleForRun", BindingFlags.Static | BindingFlags.Public)!;
        var applyForRunCalls = HarmonyLib.PatchProcessor.GetOriginalInstructions(applyForRun);
        Require(applyForRunCalls.All(instruction => instruction.operand is not MethodInfo called ||
                    called.Name != "ApplyCharacterSkinBundle"),
            "开始对局不能再次把角色皮肤和怪物资源一起批量挂载；这会破坏 NCreatureVisuals 场景类型。");
        Require(service.GetMethod("RestoreCharacterSkinBundleAfterRun", BindingFlags.Static | BindingFlags.Public) != null,
            "结束或退出对局时必须恢复皮肤包应用前的卡牌和怪物预设。");

        var cleanupPatch = assembly.GetType(
            "STS2SkinChanger.Ui.CharacterSkinBundleRunCleanupPatch", true)!;
        var cleanupPrefix = cleanupPatch.GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic)!;
        Require(HarmonyLib.PatchProcessor.GetOriginalInstructions(cleanupPrefix).Any(instruction =>
                instruction.operand is MethodInfo called && called.Name == "RestoreCharacterSkinBundleAfterRun"),
            "RunManager.CleanUp 必须恢复本局临时皮肤包预设。");

        foreach (var patchName in new[] { "SingleplayerEmbarkSkinSelectorPatch", "MultiplayerEmbarkSkinSelectorPatch" })
        {
            var patch = assembly.GetType("STS2SkinChanger.Ui." + patchName, true)!;
            var prefix = patch.GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic)!;
            var calls = HarmonyLib.PatchProcessor.GetOriginalInstructions(prefix);
            Require(calls.Any(instruction => instruction.operand is MethodInfo called &&
                        called.Name == "ApplySelectedCharacterSkinBundleForRun"),
                $"{patchName} 必须在开始对局前应用选中的皮肤包。");
        }

        var localization = assembly.GetType("STS2SkinChanger.Core.ModLocalization", true)!;
        var packs = (IReadOnlyDictionary<string, string[]>)localization.GetField(
            "BundlePacks", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var textType = assembly.GetType("STS2SkinChanger.Core.ModText", true)!;
        var first = (int)Enum.Parse(textType, "CharacterSkinBundle");
        var last = (int)Enum.Parse(textType, "BundleScopeConflict");
        string[] languages = ["eng", "zhs", "zht", "deu", "esp", "fra", "ita", "jpn",
            "kor", "pol", "ptb", "rus", "spa", "tha", "tur"];
        Require(packs.Count == languages.Length && languages.All(language =>
            packs.TryGetValue(language, out var values) && values.Length == last - first + 1 &&
            values.All(value => !string.IsNullOrWhiteSpace(value))), "皮肤包的全部文本必须覆盖工坊全部 15 种语言。");
        var missingPresetIndex = (int)Enum.Parse(textType, "BundleMissingPreset") - first;
        Require(packs.Values.All(values => string.Format(values[missingPresetIndex], "PresetName").Contains("PresetName")),
            "每种语言的缺失预设提示必须包含实际预设名称。");
        Console.WriteLine("Skin bundle contracts passed: immediate character apply, run-scoped presets, yellow list text, restore cleanup, 15 languages.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
