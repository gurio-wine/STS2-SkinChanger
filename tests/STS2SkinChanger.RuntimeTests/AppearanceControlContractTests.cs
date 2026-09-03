using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using STS2SkinChanger;

internal static class AppearanceControlContractTests
{
    internal static void Run()
    {
        var assembly = typeof(Entry).Assembly;
        var harmony = new Harmony("Gurio.SkinChanger.Tests.AppearanceControlContracts");
        var patches = new[]
        {
            ("CharacterAppearancePauseMenuPatch", typeof(NPauseMenu), "_Ready"),
            ("CharacterAppearancePauseMenuPatch", typeof(NPauseMenu), "OnSubmenuOpened"),
            ("PauseMenuAppearanceHoldPressPatch", typeof(NClickableControl), "OnPressHandler"),
            ("PauseMenuAppearanceResumeHoldPatch", typeof(NPauseMenu), "OnBackOrResumeButtonPressed")
        };
        try
        {
            foreach (var patchName in patches.Select(patch => patch.Item1).Distinct())
            {
                var patch = assembly.GetType("STS2SkinChanger.Ui." + patchName, throwOnError: true)!;
                harmony.CreateClassProcessor(patch).Patch();
            }
            foreach (var (_, type, methodName) in patches)
            {
                var target = AccessTools.Method(type, methodName) ??
                             throw new InvalidOperationException("游戏缺少外观交互目标：" + methodName);
                if (Harmony.GetPatchInfo(target)?.Owners.Contains(harmony.Id) != true)
                {
                    throw new InvalidOperationException("外观交互补丁未挂上：" + methodName);
                }
            }
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
        }

        var configType = assembly.GetType("STS2SkinChanger.Core.SkinConfig", throwOnError: true)!;
        var config = JsonSerializer.Deserialize("""
            {"ShowInRunAppearanceEntry":false,"CharacterSelectorTopRight":true,
             "CharacterSkinSelectorX":0.2,"CharacterSkinSelectorY":0.3,
             "CharacterSkinMergeX":0.7,"CharacterSkinMergeY":0.8,
             "Selections":{"SILENT":"test-skin"}}
            """, configType)!;
        var roundTrip = JsonSerializer.Deserialize(JsonSerializer.Serialize(config, configType), configType)!;
        foreach (var (field, expected) in new[]
                 { ("CharacterSkinSelectorX", 0.2f), ("CharacterSkinSelectorY", 0.3f),
                   ("CharacterSkinMergeX", 0.7f), ("CharacterSkinMergeY", 0.8f) })
        {
            if (configType.GetProperty(field)!.GetValue(roundTrip) is not float value || value != expected)
            {
                throw new InvalidOperationException("选角按钮位置没有独立持久化：" + field);
            }
        }
        if (!Equals(configType.GetProperty("ShowInRunAppearanceEntry")!.GetValue(roundTrip), false) ||
            !Equals(configType.GetProperty("CharacterSelectorTopRight")!.GetValue(roundTrip), true))
        {
            throw new InvalidOperationException("旧版隐藏入口、位置偏好不能在配置更新时丢失。");
        }

        // Every published language needs both gestures explained, without relying on fallback.
        var localization = assembly.GetType("STS2SkinChanger.Core.ModLocalization", throwOnError: true)!;
        foreach (var field in new[] { "HideAppearanceHoldHintTexts", "ShowAppearanceHoldHintTexts" })
        {
            var values = (IReadOnlyDictionary<string, string>)localization.GetField(
                field, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            string[] languages = ["eng", "zhs", "zht", "deu", "esp", "fra", "ita", "jpn",
                "kor", "pol", "ptb", "rus", "spa", "tha", "tur"];
            if (languages.Any(language => !values.TryGetValue(language, out var text) ||
                                          string.IsNullOrWhiteSpace(text)))
            {
                throw new InvalidOperationException("长按提示缺少本地化：" + field);
            }
        }
        Console.WriteLine("Appearance control contracts passed: native press/resume hooks, persistence, 15 languages.");
    }
}
