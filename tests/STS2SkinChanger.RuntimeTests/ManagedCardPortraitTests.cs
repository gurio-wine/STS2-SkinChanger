using System.Collections;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards;
using STS2SkinChanger;

internal static class ManagedCardPortraitTests
{
    private static readonly Assembly Mod = typeof(Entry).Assembly;

    public static void Run()
    {
        var assembly = typeof(ManagedCardPortraitTests).Assembly;
        var portraits = ScanPortraits(Path.GetDirectoryName(assembly.Location)!, assembly.GetName().Name!);
        Require((string?)portraits["Infection"] == "res://fixture/images/infection.png",
            "以 GetType().Name 查询字符串字典的卡图必须识别，不能仅支持 typeof 键。");
        Require((string?)portraits["Burn"] == "res://fixture/images/burn.png",
            "现有 typeof(Card) 字典接管不能回归。");
        Require((string?)portraits["Wound"] == "res://fixture/images/wound.png",
            "GetType().FullName 的字典键需要转换成目录采用的卡牌类名。");
        Require(!portraits.Contains("Dazed") && !portraits.Contains("StrikeIronclad"),
            "无卡图目标或无类名查询的字符串字典不能误接管。");
        var presentations = (IDictionary)AccessTools.Method(
            Mod.GetType("STS2SkinChanger.Catalog.ManagedCardPresentationScanner", true)!, "Scan")
            .Invoke(null, [Path.GetDirectoryName(assembly.Location), new[] { "Infection", "Dazed", "Burn" }, null])!;
        Require(presentations.Contains("Infection") &&
                Equals(Property(presentations["Infection"]!, "BuiltInOverlayVisible"), false),
            "明确关闭卡牌内置覆盖层的 getter 补丁必须转换为本卡呈现声明。");
        Require(!presentations.Contains("Dazed") && !presentations.Contains("Burn"),
            "条件补丁或其它属性不能被解释成无条件隐藏内置覆盖层。");
        CheckOverlayRestoration();
        Console.WriteLine("Managed portrait mappings passed: string/type keys, negative cases and scoped overlay intent.");
    }

    private static void CheckOverlayRestoration()
    {
        var type = Mod.GetType("STS2SkinChanger.Ui.CardBuiltInOverlayVisibility");
        Require(type != null, "内置覆盖层必须独立保存真实原始显隐，不能把已经隐藏的状态重新捕获为原皮。");
        var state = Activator.CreateInstance(type!)!;
        var resolve = type!.GetMethod("Resolve")!;
        var model = new object();
        var overlay = new object();
        bool Apply(object m, object n, bool visible, bool? choice) =>
            (bool)resolve.Invoke(state, [m, n, visible, choice])!;
        Require(!Apply(model, overlay, true, false), "选择关闭内置效果的皮肤应隐藏原版覆盖层。");
        Require(!Apply(model, overlay, false, false), "重复刷新不能改写最初可见状态。");
        Require(Apply(model, overlay, false, null), "切回原版或没有该声明的其它皮肤必须恢复原版覆盖层。");
        Require(!Apply(model, overlay, true, false), "切回该皮肤应再次隐藏。");
        var replacement = new object();
        Require(!Apply(model, replacement, true, false) && Apply(model, replacement, false, null),
            "原版 ReloadOverlay 新建的节点也要应用选择，并独立恢复。");
        Require(!Apply(new object(), replacement, false, null), "节点复用到另一张卡时不能恢复前一张卡的显隐。");
        Require(!Apply(model, new object(), false, false), "原本就隐藏的覆盖层保持隐藏。");
        var controls = Mod.GetType("STS2SkinChanger.Ui.CardSkinControls", true)!;
        var apply = AccessTools.Method(controls, "ApplyBuiltInOverlay");
        Require(AccessTools.Field(typeof(NCard), "_cardOverlay") != null && AccessTools.Method(typeof(NCard), "ReloadOverlay") != null,
            "两游戏版本都必须具有已核验的本卡覆盖层入口。");
        var harmony = new Harmony("SkinChanger.Tests.BuiltInOverlay");
        try
        {
            var patch = Mod.GetType("STS2SkinChanger.Ui.CardBuiltInOverlayPatch", true)!;
            harmony.CreateClassProcessor(patch).Patch();
            Require(PatchProcessor.GetOriginalInstructions(AccessTools.Method(patch, "Postfix"))
                .Any(i => Equals(i.operand, apply)), "覆盖层独立重建后也必须走相同显隐恢复流程。");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }

    public static void Audit(string gamePack, string root)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Parasitophobia.json")));
        var id = manifest.RootElement.GetProperty("id").GetString()!;
        var mappings = ScanPortraits(root, id);
        Require(mappings.Count == 1 && (string?)mappings["Infection"] == "res://Parasitophobia/Images/Infection.png",
            "实包必须且只能识别感染的私有卡图。");
        var descriptorType = Mod.GetType("STS2SkinChanger.Catalog.SkinModDescriptor", true)!;
        var descriptors = Array.CreateInstance(descriptorType, 1);
        descriptors.SetValue(Activator.CreateInstance(descriptorType, id, id, Path.Combine(root, id + ".pck"), false, root, true, null), 0);
        var catalogType = Mod.GetType("STS2SkinChanger.Catalog.SkinCatalog", true)!;
        using var catalog = (IDisposable)AccessTools.Method(catalogType, "Build").Invoke(null, [gamePack, descriptors])!;
        var entryType = Mod.GetType("STS2SkinChanger.Catalog.CardCatalogEntry", true)!;
        var entries = Array.CreateInstance(entryType, 2);
        entries.SetValue(Activator.CreateInstance(entryType, "Infection", "res://images/packed/card_portraits/status/infection.png", "status", "status", "status"), 0);
        entries.SetValue(Activator.CreateInstance(entryType, "Burn", "res://images/packed/card_portraits/status/burn.png", "status", "status", "status"), 1);
        AccessTools.Method(catalogType, "FinalizeCardGroups").Invoke(catalog, [entries]);
        var groups = ((IEnumerable)Property(catalog, "CardGroups")!).Cast<object>().ToArray();
        var status = groups.Single(group => (string?)Property(group, "Id") == "status");
        var option = ((IEnumerable)Property(status, "Options")!).Cast<object>().Single(o => (string?)Property(o, "Id") == id);
        var portraits = (IDictionary)Property(option, "NormalPortraits")!;
        Require(portraits.Count == 1 && portraits.Contains("Infection") && !portraits.Contains("Burn"),
            "图鉴归类只能包含实际替换的卡，不能扩散到同分类其它卡。");
        var presentation = ((IDictionary)Property(option, "CardPresentations")!)["Infection"]!;
        Require(Equals(Property(presentation, "BuiltInOverlayVisible"), false) &&
                Equals(Property(presentation, "UseAncientLayout"), false), "感染只替换卡图和内置虫子覆盖层，不添加先古外观。");
        var providerOverlay = (IDictionary)AccessTools.Method(catalogType, "BuildCardProviderNamespaceOverlay")
            .Invoke(catalog, [new[] { id }])!;
        Require(providerOverlay.Keys.Cast<string>().Any(path => path.Contains("Infection.png", StringComparison.OrdinalIgnoreCase)),
            "私有图片的 import/纹理依赖必须进入隔离资源包，不能只有列表名称。");
        VerifySelectedOwnership(catalog, id);
        Console.WriteLine("Parasitophobia actual package passed: Infection in status, isolated portrait dependencies and no Ancient layout.");
    }

    private static void VerifySelectedOwnership(object catalog, string provider)
    {
        var service = Mod.GetType("STS2SkinChanger.Core.SkinService", true)!;
        var configProperty = AccessTools.Property(service, "Config");
        var catalogProperty = AccessTools.Property(service, "Catalog");
        var cache = AccessTools.Field(service, "_cardLookupCache");
        var allCards = AccessTools.Field(typeof(ModelDb), "_allCards");
        var oldConfig = configProperty.GetValue(null);
        var oldCatalog = catalogProperty.GetValue(null);
        var oldCache = cache.GetValue(null);
        var oldCards = allCards.GetValue(null);
        var infection = new Infection();
        var burn = new Burn();
        foreach (var card in new CardModel[] { infection, burn })
        {
            AccessTools.Field(typeof(AbstractModel), "<Id>k__BackingField").SetValue(card, new ModelId("CARD", card.GetType().Name.ToUpperInvariant()));
            AccessTools.Field(typeof(CardModel), "_pool").SetValue(card, new StatusCardPool());
        }
        try
        {
            catalogProperty.SetValue(null, catalog);
            allCards.SetValue(null, new CardModel[] { infection, burn });
            foreach (var choice in new[] { provider, "__base__", provider, "__base__" })
            {
                configProperty.SetValue(null, AccessTools.Method(configProperty.PropertyType, "Deserialize").Invoke(null,
                    [JsonSerializer.Serialize(new { Selections = new Dictionary<string, string> { ["cards:item:card.infection"] = choice } })]));
                cache.SetValue(null, Activator.CreateInstance(cache.FieldType));
                var request = AccessTools.Method(service, "ResolveCardPortraitRequest").Invoke(null, [infection])!;
                var presentation = AccessTools.Method(service, "GetCardPresentation").Invoke(null, [infection]);
                Require(Equals(Property(request, "UseSelectedProvider"), choice == provider), "单卡往返切换必须实际切换图片所有者。");
                Require(choice == provider
                    ? (string?)Property(request, "ResourcePath") == "res://Parasitophobia/Images/Infection.png" &&
                      presentation != null && Equals(Property(presentation, "BuiltInOverlayVisible"), false)
                    : (string?)Property(request, "ResourcePath") == infection.PortraitPath && presentation == null,
                    "切回原版必须同时退出自定义卡图和隐藏虫子的声明。");
                Require(AccessTools.Method(service, "GetCardPresentation").Invoke(null, [burn]) == null &&
                        infection.Rarity == CardRarity.Status && infection.HasBuiltInOverlay && infection.HasTurnEndInHandEffect,
                    "不能污染同分类卡牌，不能修改真实稀有度、覆盖层属性或回合末效果。");
            }
        }
        finally
        {
            configProperty.SetValue(null, oldConfig);
            catalogProperty.SetValue(null, oldCatalog);
            cache.SetValue(null, oldCache);
            allCards.SetValue(null, oldCards);
        }
    }

    private static IDictionary ScanPortraits(string root, string id) => (IDictionary)AccessTools.Method(
        Mod.GetType("STS2SkinChanger.Catalog.ManagedCardPortraitReplacementScanner", true)!, "Scan").Invoke(null, [root, id])!;
    private static object? Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    [HarmonyPatch(typeof(CardModel), "PortraitPath", MethodType.Getter)]
    private static class NamedPortraits
    {
        private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
        { ["Infection"] = "res://fixture/images/infection.png" };
        private static void Postfix(CardModel __instance, ref string __result)
        {
            if (Map.TryGetValue(__instance.GetType().Name, out var path)) __result = path;
        }
    }

    [HarmonyPatch(typeof(CardModel), "PortraitPath", MethodType.Getter)]
    private static class TypedPortraits
    {
        private static readonly Dictionary<Type, string> Map = new() { [typeof(Burn)] = "res://fixture/images/burn.png" };
        private static void Postfix(CardModel __instance, ref string __result)
        {
            if (Map.TryGetValue(__instance.GetType(), out var path)) __result = path;
        }
    }

    [HarmonyPatch(typeof(CardModel), "PortraitPath", MethodType.Getter)]
    private static class QualifiedPortraits
    {
        private static readonly Dictionary<string, string> Map = new()
        { ["MegaCrit.Sts2.Core.Models.Cards.Wound"] = "res://fixture/images/wound.png" };
        private static void Postfix(CardModel __instance, ref string __result)
        {
            if (Map.TryGetValue(__instance.GetType().FullName!, out var path)) __result = path;
        }
    }

    private static class UnrelatedLookup
    {
        private static readonly Dictionary<string, string> Map = new() { ["Dazed"] = "res://fixture/ui/dazed.png" };
        public static string? Lookup(object value) => Map.TryGetValue(value.GetType().Name, out var path) ? path : null;
    }

    [HarmonyPatch(typeof(CardModel), "PortraitPath", MethodType.Getter)]
    private static class UnrelatedPortraitLookup
    {
        private static readonly Dictionary<string, string> Map = new() { ["StrikeIronclad"] = "res://fixture/ui/strike.png" };
        public static string? Lookup(object value) => Map.TryGetValue(value.GetType().ToString(), out var path) ? path : null;
    }

    [HarmonyPatch(typeof(Infection), "HasBuiltInOverlay", MethodType.Getter)]
    private static class OverlayOff
    {
        private static void Postfix(ref bool __result) => __result = false;
    }

    [HarmonyPatch(typeof(Dazed), "HasBuiltInOverlay", MethodType.Getter)]
    private static class ConditionalOverlay
    {
        public static bool Enabled = false;
        private static void Postfix(ref bool __result) { if (Enabled) __result = false; }
    }

    [HarmonyPatch(typeof(Burn), "HasTurnEndInHandEffect", MethodType.Getter)]
    private static class NotVisual
    {
        private static void Postfix(ref bool __result) => __result = false;
    }
}
