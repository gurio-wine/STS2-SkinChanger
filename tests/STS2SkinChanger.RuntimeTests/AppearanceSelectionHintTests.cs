using System.Collections;
using System.Reflection;
using HarmonyLib;
using STS2SkinChanger;

internal static class AppearanceSelectionHintTests
{
    internal static void Run()
    {
        var assembly = typeof(Entry).Assembly;
        var localization = assembly.GetType("STS2SkinChanger.Core.ModLocalization", true)!;
        var format = AccessTools.Method(localization, "FormatAppearanceTargetHint")
            ?? throw new InvalidOperationException("外观选择提示尚未根据可选目标生成。");
        var kind = assembly.GetType("STS2SkinChanger.Core.AppearanceTargetKind", true)!;
        string Format(string language, params string[] names)
        {
            var values = Array.CreateInstance(kind, names.Length);
            for (var i = 0; i < names.Length; i++) values.SetValue(Enum.Parse(kind, names[i]), i);
            return (string)format.Invoke(null, [values, language])!;
        }
        Require(Format("zhs", "Merchant", "Character", "Merchant") ==
                "请选择要调整外观的目标：角色、商人。",
            "商店提示只能列出角色、商人，去重并保持稳定顺序。");
        Require(Format("zhs", "MapBoss") == "请选择要调整外观的目标：地图上的 Boss 图标。",
            "地图提示不能列出被地图遮住的战斗目标。");
        Require(Format("zhs", "Monster", "Character", "Companion") ==
                "请选择要调整外观的目标：角色、怪物、同伴。",
            "战斗提示应随实际出现的怪物及同伴变化。");
        Require(Format("zhs", "Ancient") == "请选择要调整外观的目标：先古之民。" &&
                Format("zhs") == "当前没有可调整的目标。",
            "先古场景与空目标不能继续显示完整功能清单。");
        Require(Format("jpn", "MapBoss") == "外見を変更する対象を選択：マップのボスアイコン" &&
                Format("missing", "Character") == "Choose what to customize: characters.",
            "动态目标提示必须正确使用当前语言及未知语言回退。");
        var packs = (IDictionary)AccessTools.Field(localization, "AppearanceTargetHintPacks").GetValue(null)!;
        Require(packs.Count == 15, "动态目标提示必须覆盖全部 15 种语言。");
        foreach (string language in packs.Keys)
        {
            var singleKinds = Enum.GetNames(kind).Select(name => Format(language, name)).ToArray();
            Require(singleKinds.All(text => !string.IsNullOrWhiteSpace(text) && !text.Contains("{0}")) &&
                    singleKinds.Distinct().Count() == 6 && !string.IsNullOrWhiteSpace(Format(language)),
                "目标类型或空状态缺少独立翻译：" + language);
        }
        var screen = assembly.GetType("STS2SkinChanger.Ui.CharacterAppearanceScreen", true)!;
        foreach (var consumer in new[] { "TrySelectTarget", "RefreshSelectionHint" })
        {
            Require(PatchProcessor.GetOriginalInstructions(AccessTools.Method(screen, consumer))
                    .Any(instruction => instruction.operand is MethodInfo called && called.Name == "GetSelectableTargets"),
                "提示与点击必须共用目标判断，不能各自猜当前房间类型：" + consumer);
        }
        Console.WriteLine("Appearance selection hints passed: live target types, stable order, empty state, 15 languages and shared hit testing.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
