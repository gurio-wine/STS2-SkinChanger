using System.Collections;
using System.Reflection;
using HarmonyLib;
using STS2SkinChanger;

internal static class WorkshopEntryTests
{
    public static void Run()
    {
        var assembly = typeof(Entry).Assembly;
        var texts = assembly.GetType("STS2SkinChanger.Core.WorkshopText", true)!;
        var key = assembly.GetType("STS2SkinChanger.Core.WorkshopTextKey", true)!;
        foreach (var language in new[] { "eng", "zhs", "zht", "deu", "esp", "spa", "fra", "ita", "jpn", "kor", "pol", "ptb", "rus", "tha", "tur" })
        {
            var title = (string)texts.GetMethod("ForLanguage")!.Invoke(null, [language, Enum.Parse(key, "Title")])!;
            Require(title.StartsWith('☼') && !title.Contains('…') && !title.Contains("..."),
                $"{language} 的工坊标题和入口必须统一为带 ☼ 的名称，不能遗留省略号。");
        }
        var localization = assembly.GetType("STS2SkinChanger.Core.ModLocalization", true)!;
        var follow = Enum.Parse(assembly.GetType("STS2SkinChanger.Core.ModText", true)!, "FollowCategory");
        var packs = (IDictionary)AccessTools.Field(localization, "Packs").GetValue(null)!;
        foreach (DictionaryEntry pack in packs)
            Require(((string)pack.Value!.GetType().GetMethod("Get")!.Invoke(pack.Value, [follow])!).StartsWith('↷'),
                $"{pack.Key} 的跟随分类必须由统一文案入口添加 ↷。");

        var style = assembly.GetType("STS2SkinChanger.Core.SkinOptionStylePolicy");
        Require(style != null, "所有皮肤选择器应共享强调色判断，不得遗漏单卡、怪物或事件。");
        foreach (var id in new[] { "__inherit__", "__monster_category__", "__event_category__", "__workshop__" })
            Require((bool)style!.GetMethod("IsAccented")!.Invoke(null, [id])!, $"{id} 应使用强调色。");
        Require(!(bool)style!.GetMethod("IsAccented")!.Invoke(null, ["skin:ordinary"])!, "普通皮肤名不能被一并染色。");

        foreach (var (type, method) in new[] {
            ("CardSkinControls", "BuildPriorityOverlay"),
            ("ContextualSkinControls", "BuildMonsterPriorityOverlay"),
            ("AncientCompendiumScreen", "BuildEventPriorityList"),
            ("CharacterSkinCompositionControls", "BuildEditor"),
            ("CharacterSkinBundleControls", "AddContentSection") })
            Require(Calls(AccessTools.Method(assembly.GetType("STS2SkinChanger.Ui." + type, true), method), "CreatePriorityButton"),
                $"{type}.{method} 缺少可见的工坊入口。");
        var entry = assembly.GetType("STS2SkinChanger.Ui.SkinWorkshopEntry", true)!;
        Require(Calls(AccessTools.Method(entry, "CreatePriorityButton"), "AccentText"), "优先级工坊按钮应跟随主题强调色。");
        Require(Calls(AccessTools.Method(entry, "Append"), "IsAccented"), "下拉菜单不能继续使用遗漏跟随分类的旧染色判断。");
        foreach (var (type, method) in new[] {
            ("CardInspectSkinControls", "Sync"),
            ("CharacterAppearanceScreen", "PopulateSkinDropdown"),
            ("AncientCompendiumScreen", "PopulateSkinDropdown") })
            Require(Calls(AccessTools.Method(assembly.GetType("STS2SkinChanger.Ui." + type, true), method), "RefreshSelectionColor"),
                $"{type} 收起时的当前选择也要及时显示强调色。");

        // Every player-facing skin list must take its order and its dimming from the one shared
        // priority projection; a list that re-adds options itself would silently drift from it.
        // The bundle editor is excluded on purpose: it only ever edits character groups, which
        // have no priority screen, so it must keep the catalogue order untouched.
        foreach (var (type, method) in new[] {
            ("AncientCompendiumScreen", "PopulateSkinDropdown"),
            ("CharacterAppearanceScreen", "PopulateSkinDropdown"),
            ("CardInspectSkinControls", "PopulateOptions"),
            ("ContextualSkinControls", "Populate") })
            Require(Calls(AccessTools.Method(assembly.GetType("STS2SkinChanger.Ui." + type, true), method), "Bind"),
                $"{type}.{method} 必须复用统一的皮肤优先级投影，不能各自重排或各自判定淡化。");
        var dimming = assembly.GetType("STS2SkinChanger.Ui.SkinChoiceDimming", true)!;
        Require(Calls(AccessTools.Method(dimming, "Row"), "IsUnchecked"),
            "淡化的行必须查同一份优先级状态，不能凭名字或序号猜测。");
        Require(!Calls(AccessTools.Method(dimming, "Row"), "IsAccented") &&
            !Calls(AccessTools.Method(dimming, "Row"), "ChoiceColor"),
            "淡化只叠加透明度，不能替换掉原有的强调色或随机排除判断。");
        // Bind only records the state; the colored popup list is what actually paints it. A native
        // PopupMenu cannot tint single rows. The contextual character selector paints through its
        // own bundle popup list, so it is exempt here.
        foreach (var (type, method) in new[] {
            ("AncientCompendiumScreen", "PopulateSkinDropdown"),
            ("CharacterAppearanceScreen", "PopulateSkinDropdown"),
            ("CardInspectSkinControls", "PopulateOptions") })
            Require(Calls(AccessTools.Method(assembly.GetType("STS2SkinChanger.Ui." + type, true), method), "Attach"),
                $"{type}.{method} 的淡化必须经由共享的彩色选项列表渲染，纯文本弹出画不出逐行透明度。");

        var panel = assembly.GetType("STS2SkinChanger.Ui.SkinWorkshopPanel", true)!;
        Require(AccessTools.DeclaredMethod(panel, "Show").GetParameters().Any(p => p.Name == "region"),
            "地区优先级入口必须传递地区筛选，不能将地区 ID 当作怪物/事件 ID。");
        var normalize = AccessTools.Method(panel, "ResolveEntryRegion");
        Require(normalize != null, "入口地区应与工坊地区键统一，避免大小写和区域前缀不一致。");
        Require((string)normalize!.Invoke(null, ["act:overgrowth", new[] { "OVERGROWTH", "events" }])! == "OVERGROWTH" &&
            (string)normalize.Invoke(null, ["events", new[] { "OVERGROWTH", "events" }])! == "events" &&
            (string)normalize.Invoke(null, ["missing", new[] { "OVERGROWTH" }])! == "",
            "怪物地区、事件地区和缺失地区应得到有效的工坊筛选键。");

        using var resource = assembly.GetManifestResourceStream("STS2SkinChanger.Data.workshop-catalog.json")!;
        using var reader = new StreamReader(resource);
        var catalog = reader.ReadToEnd();
        var policy = assembly.GetType("STS2SkinChanger.Core.WorkshopCatalogPolicy", true)!;
        var ids = (ulong[])policy.GetMethod("FilterIds")!.Invoke(null, [catalog, "event", "event:abyssal_baths"])!;
        Require(ids.Contains(3765802910UL), "NSFW 原版事件替换必须已收录并可由事件入口找到。");
        Console.WriteLine("Workshop entry and skin-choice styling tests passed.");
    }

    private static bool Calls(MethodBase method, string target) => PatchProcessor.GetOriginalInstructions(method)
        .Any(i => i.operand is MethodInfo call && call.Name == target);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
