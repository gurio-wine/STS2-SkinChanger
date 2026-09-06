using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Cards;
using STS2SkinChanger;

internal static class AncientCardPriorityTests
{
    private static readonly Assembly Mod = typeof(Entry).Assembly;
    private static readonly Type Service = Mod.GetType("STS2SkinChanger.Core.SkinService", true)!;
    private static readonly Type CatalogType = Mod.GetType("STS2SkinChanger.Catalog.SkinCatalog", true)!;
    private static readonly Type GroupType = Mod.GetType("STS2SkinChanger.Catalog.CardSkinGroup", true)!;
    private static readonly Type OptionType = Mod.GetType("STS2SkinChanger.Catalog.CardSkinOption", true)!;

    internal static void Run()
    {
        var configProperty = AccessTools.Property(Service, "Config");
        var catalogProperty = AccessTools.Property(Service, "Catalog");
        var oldConfig = configProperty.GetValue(null);
        var oldCatalog = catalogProperty.GetValue(null);
        var cacheField = AccessTools.Field(Service, "_cardLookupCache");
        var oldCache = cacheField.GetValue(null);
        var cardsField = AccessTools.Field(typeof(ModelDb), "_allCards");
        var oldCards = cardsField.GetValue(null);
        var coverage = (IDictionary)AccessTools.Field(Service, "CardCoverageCache").GetValue(null)!;
        var oldCoverage = coverage.Cast<DictionaryEntry>().ToArray();
        try
        {
            var dedicated = Card(new BiasedCognition(), new DefectCardPool(), "BIASED_COGNITION");
            var ordinary = Card(new StrikeDefect(), new DefectCardPool(), "STRIKE_DEFECT");
            var shared = Card(new Apotheosis(), new ColorlessCardPool(), "APOTHEOSIS");
            var modded = Card(new Wither(), new ModdedPool(), "WITHER");
            var uncovered = Card(new Corruption(), new IroncladCardPool(), "CORRUPTION");
            var cards = new CardModel[] { dedicated, ordinary, shared, modded, uncovered };
            cardsField.SetValue(null, cards);
            object Option(string id, params string[] types) => NewOption(id, types);
            var first = Option("skin:character", "BiasedCognition");
            var second = Option("skin:ancient", "BiasedCognition", "Apotheosis");
            var unrelated = Option("skin:ordinary", "StrikeDefect");
            var catalog = NewCatalog();
            AddGroup(catalog, "defect", first, second, unrelated);
            AddGroup(catalog, "ancients", first, second, Option("skin:modded", "Wither"));
            AddGroup(catalog, "modded", Option("skin:modded", "Wither"));
            catalogProperty.SetValue(null, catalog);

            void Configure(string json)
            {
                configProperty.SetValue(null, AccessTools.Method(configProperty.PropertyType, "Deserialize")
                    .Invoke(null, [json]));
                cacheField.SetValue(null, Activator.CreateInstance(cacheField.FieldType));
                coverage.Clear();
            }
            string Selected(CardModel card) => (string)AccessTools.Method(Service,
                "GetEffectiveCardSelection", [typeof(CardModel)]).Invoke(null, [card])!;
            bool Belongs(CardModel card, string group) => (bool)AccessTools.Method(Service, "CardBelongsToGroup")
                .Invoke(null, [card, group])!;

            Configure("""
                {"CardSkinPriorities":{
                "defect":[{"OptionId":"skin:character","Enabled":true},{"OptionId":"skin:ancient","Enabled":true}],
                "ancients":[{"OptionId":"skin:ancient","Enabled":true},{"OptionId":"skin:character","Enabled":true}]}}
                """);
            Require(Selected(dedicated) == "skin:character", "角色专属先古牌应优先角色分类，不能直接用先古分类。");
            Require(Selected(shared) == "skin:ancient", "通用先古牌不得继承当前玩家或其它角色的分类。");
            Require(Selected(modded) == "skin:modded", "Mod 角色牌池也必须支持先古牌继承。");
            Require(Belongs(dedicated, "defect") && Belongs(dedicated, "ancients") &&
                    !Belongs(shared, "defect") && !Belongs(ordinary, "ancients"),
                "覆盖统计与刷新应同时包括角色专属先古牌，不扩散到普通牌或其它角色。");
            var coverageRows = ((IEnumerable)AccessTools.Method(Service, "GetCardPriorityOptions")
                .Invoke(null, ["defect"])!).Cast<object>().ToArray();
            var coverageRow = coverageRows.Single(row => Value<string>(row, "OptionId") == "skin:character");
            Require(Value<int>(coverageRow, "Coverage") == 1 && Value<int>(coverageRow, "TotalCards") == 2,
                "角色分类的覆盖数应计入专属先古牌，但不得计入通用先古牌。");
            var ancientCoverage = ((IEnumerable)AccessTools.Method(Service, "GetCardPriorityOptions")
                .Invoke(null, ["ancients"])!).Cast<object>().First();
            Require(Value<int>(ancientCoverage, "TotalCards") == 4,
                "先古分类的总卡数必须包含没有任何皮肤来源的先古牌。");
            var sourcesMethod = AccessTools.Method(Service, "GetCardSkinSources");
            foreach (var display in new[] { "defect", "ancients" })
            {
                var sources = ((IEnumerable)sourcesMethod.Invoke(null, [dedicated, display])!).Cast<object>().ToArray();
                var current = sources.Single(source => Value<bool>(source, "IsCurrent"));
                Require(Value<string>(current, "OptionId") == "skin:character" &&
                        Value<int>(current, "ColorIndex") == (display == "defect" ? 0 : 1),
                    "同一张牌跨分类必须保持来源一致，并使用所在列表的颜色编号。");
            }
            var request = AccessTools.Method(Service, "ResolveCardPortraitRequest").Invoke(null, [dedicated])!;
            Require((string)AccessTools.Property(request.GetType(), "ResourcePath").GetValue(request)! ==
                "res://skin:character/BiasedCognition.png", "卡图必须来自同一个获胜来源。");
            var priorities = (IDictionary)AccessTools.Property(configProperty.PropertyType, "CardSkinPriorities")
                .GetValue(configProperty.GetValue(null))!;
            var characterEntries = (IList)priorities["defect"]!;
            (characterEntries[0], characterEntries[1]) = (characterEntries[1], characterEntries[0]);
            Require(Selected(dedicated) == "skin:ancient", "重排优先级应即时改变已缓存卡牌的来源，不依赖退出图鉴。");
            characterEntries = (IList)priorities["defect"]!;
            (characterEntries[0], characterEntries[1]) = (characterEntries[1], characterEntries[0]);
            Require(Selected(dedicated) == "skin:character", "反复重排不能保留上一次解析结果。");

            // A provider absent from the higher category can fill its gaps; a disabled one cannot.
            var defectOptions = Options(Groups(catalog).Cast<object>().First(g => Id(g) == "defect"));
            defectOptions.Remove(second);
            Configure("""
                {"CardSkinPriorities":{
                "defect":[{"OptionId":"skin:character","Enabled":false},{"OptionId":"skin:ordinary","Enabled":true}],
                "ancients":[{"OptionId":"skin:ancient","Enabled":true},{"OptionId":"skin:character","Enabled":true}]}}
                """);
            Require(Selected(dedicated) == "skin:ancient", "角色分类没有覆盖该牌时应回退先古分类。");
            Require((string?)AccessTools.Method(Service, "GetCardInheritedGroupId").Invoke(null, [dedicated]) == "ancients",
                "来源文字必须指向真正提供皮肤的分类。");
            defectOptions.Add(second);
            Configure("""
                {"CardSkinPriorities":{
                "defect":[{"OptionId":"skin:character","Enabled":false},{"OptionId":"skin:ancient","Enabled":false},{"OptionId":"skin:ordinary","Enabled":true}],
                "ancients":[{"OptionId":"skin:ancient","Enabled":true},{"OptionId":"skin:character","Enabled":true}]}}
                """);
            Require(Selected(dedicated) == "__base__", "已禁用的来源不得经先古分类复活。");
            Configure("""
                {"CardSkinPriorities":{
                "defect":[{"OptionId":"skin:character","Enabled":false},{"OptionId":"skin:ancient","Enabled":false},{"OptionId":"skin:ordinary","Enabled":false}],
                "ancients":[{"OptionId":"skin:ancient","Enabled":true}]}}
                """);
            Require(Selected(dedicated) == "__base__", "角色分类全原版必须终止回退。");
            // A newly discovered Ancient-only source must not undo an old all-original choice.
            Configure("""
                {"CardSkinPriorities":{"defect":[{"OptionId":"skin:ordinary","Enabled":false}]}}
                """);
            Require(Selected(dedicated) == "__base__", "旧分类全关闭时，新加入的先古来源不能自动启用。");
            Configure("""{"Selections":{"cards:defect":"__base__"},"CardPriorityDefaultsVersion":1}""");
            Require(Selected(dedicated) == "__base__", "旧格式明确原版不能被默认启用覆盖。");
            var selections = (IDictionary)AccessTools.Property(configProperty.PropertyType, "Selections")
                .GetValue(configProperty.GetValue(null))!;
            selections["cards:item:card.biased_cognition"] = "skin:ancient";
            Require(Selected(dedicated) == "skin:ancient", "单卡明确指定优先于分类禁用。");
            selections["cards:item:card.biased_cognition"] = "__base__";
            Require(Selected(dedicated) == "__base__", "单卡原版必须优先于所有分类。");
            AccessTools.Method(Service, "WithCardPreviewSelection").Invoke(null,
                [dedicated, "skin:character", (Action)(() =>
                    Require(Selected(dedicated) == "skin:character", "单卡悬浮预览应临时优先于保存的原版选择。"))]);
            Require(Selected(dedicated) == "__base__", "结束悬浮预览必须恢复单卡原版，不改写分类或单卡选择。");
            Require((bool)AccessTools.Method(Service, "CardSelectionBelongsToGroup")
                    .Invoke(null, ["cards:item:card.biased_cognition", "defect"])! &&
                    (bool)AccessTools.Method(Service, "CardSelectionBelongsToGroup")
                    .Invoke(null, ["cards:item:card.biased_cognition", "ancients"])!,
                "在两个分类保存/恢复预设时，均应保留这张牌的单卡选择。");
            Require(dedicated.Rarity == MegaCrit.Sts2.Core.Entities.Cards.CardRarity.Ancient &&
                    dedicated.Pool is DefectCardPool, "外观优先级不能改真实稀有度或牌池。");

            CheckCatalogMembership(cards);
            CheckDependentCacheInvalidation();
            Console.WriteLine("Ancient card priorities passed: character-first, fallback, explicit choices, exclusions and catalog membership.");
        }
        finally
        {
            configProperty.SetValue(null, oldConfig);
            catalogProperty.SetValue(null, oldCatalog);
            cacheField.SetValue(null, oldCache);
            cardsField.SetValue(null, oldCards);
            coverage.Clear();
            foreach (var pair in oldCoverage) coverage.Add(pair.Key, pair.Value);
        }
    }

    private static void CheckCatalogMembership(CardModel[] cards)
    {
        var catalog = NewCatalog();
        var pckOptions = (IList)AccessTools.Field(CatalogType, "_pckCardOptions").GetValue(catalog)!;
        pckOptions.Add(NewOption("skin:all", ["BiasedCognition", "Apotheosis", "Wither"]));
        var frameOnly = NewOption("skin:frame", []);
        var presentationType = Mod.GetType("STS2SkinChanger.Catalog.CardPresentationDefinition", true)!;
        var presentationConstructor = presentationType.GetConstructors().Single();
        var args = presentationConstructor.GetParameters().Select(p => p.DefaultValue).ToArray();
        args[0] = true;
        args[1] = "res://frame-only/frame.png";
        var presentation = presentationConstructor.Invoke(args);
        ((IDictionary)AccessTools.Property(OptionType, "CardPresentations").GetValue(frameOnly)!)
            .Add("BiasedCognition", presentation);
        var configured = (IList)AccessTools.Field(CatalogType, "_configuredCardGroups").GetValue(catalog)!;
        var configuredGroup = Activator.CreateInstance(GroupType, ["defect", "defect"])!;
        Options(configuredGroup).Add(frameOnly);
        configured.Add(configuredGroup);
        var raw = NewOption("skin:raw", []);
        var rawAssets = (IDictionary)AccessTools.Property(OptionType, "Assets").GetValue(raw)!;
        var assetType = Mod.GetType("STS2SkinChanger.Catalog.ResourceAsset", true)!;
        foreach (var path in new[] { cards[0].PortraitPath, cards[2].PortraitPath })
            rawAssets.Add(path, Activator.CreateInstance(assetType, [path]));
        pckOptions.Add(raw);
        var entryType = Mod.GetType("STS2SkinChanger.Catalog.CardCatalogEntry", true)!;
        var entries = Array.CreateInstance(entryType, cards.Length);
        for (var i = 0; i < cards.Length; i++)
        {
            var card = cards[i];
            var pool = card.Pool.Title;
            var ancient = card.Rarity == MegaCrit.Sts2.Core.Entities.Cards.CardRarity.Ancient;
            var entry = Activator.CreateInstance(entryType, [card.GetType().Name, card.PortraitPath, pool, pool, ancient ? "ancients" : pool])!;
            AccessTools.Property(entryType, "IsCharacterPool").SetValue(entry, !card.Pool.IsColorless);
            entries.SetValue(entry, i);
        }
        AccessTools.Method(CatalogType, "FinalizeCardGroups").Invoke(catalog, [entries]);
        var groups = Groups(catalog).Cast<object>().ToDictionary(Id);
        Require(groups.ContainsKey("defect") && groups.ContainsKey("modded") && !groups.ContainsKey("colorless"),
            "先古卡图来源应进入对应原生/Mod角色列表，不应进入通用无色分类。");
        var portraits = (IDictionary)AccessTools.Property(OptionType, "NormalPortraits").GetValue(
            Options(groups["defect"]).Cast<object>().Single(option => Value<string>(option, "Id") == "skin:all"))!;
        Require(portraits.Contains("BiasedCognition") && !portraits.Contains("Apotheosis") && !portraits.Contains("Wither"),
            "镜像来源只能携带本角色专属牌，不能混入其它角色或通用先古牌。");
        foreach (var group in new[] { "defect", "ancients" })
        {
            var options = Options(groups[group]).Cast<object>().ToDictionary(option => Value<string>(option, "Id"));
            Require(options.ContainsKey("skin:frame") && options.ContainsKey("skin:raw"),
                "仅改外框/特效的声明式来源和原始 PCK 卡图都必须参与继承。");
            var selectedPresentation = ((IDictionary)AccessTools.Property(OptionType, "CardPresentations")
                .GetValue(options["skin:frame"])!)["BiasedCognition"];
            Require(ReferenceEquals(selectedPresentation, presentation), "镜像只复制来源归属，不改写它的卡面呈现定义。");
        }
        AccessTools.Property(Service, "Catalog").SetValue(null, catalog);
        var lookupCache = AccessTools.Field(Service, "_cardLookupCache");
        lookupCache.SetValue(null, Activator.CreateInstance(lookupCache.FieldType));
        var configType = AccessTools.Property(Service, "Config").PropertyType;
        AccessTools.Property(Service, "Config").SetValue(null, AccessTools.Method(configType, "Deserialize").Invoke(null,
            ["""{"Selections":{"cards:item:card.biased_cognition":"skin:frame"}}"""]));
        var request = AccessTools.Method(Service, "ResolveCardPortraitRequest").Invoke(null, [cards[0]])!;
        Require(!Value<bool>(request, "UseSelectedProvider") && Value<string>(request, "Selection") == "__base__" &&
                ReferenceEquals(AccessTools.Method(Service, "GetCardPresentation").Invoke(null, [cards[0]]), presentation),
            "仅外框来源胜出时，缺失卡图必须来自原版，不能借用下一个 Mod 的卡图。");
    }

    private static void CheckDependentCacheInvalidation()
    {
        var failures = (HashSet<string>)AccessTools.Field(Service, "FailedCardPortraitRequests").GetValue(null)!;
        var previous = failures.ToArray();
        try
        {
            failures.Clear();
            failures.Add("ancients\nskin:all\nportrait");
            failures.Add("silent\nskin:other\nportrait");
            AccessTools.Method(Service, "ClearCardPortraitCache").Invoke(null, ["defect"]);
            Require(failures.SetEquals(["silent\nskin:other\nportrait"]),
                "修改角色分类必须清掉先古卡面的失败缓存，不应清掉其它角色的缓存。");
        }
        finally { failures.Clear(); failures.UnionWith(previous); }
    }

    private static T Value<T>(object instance, string name) =>
        (T)AccessTools.Property(instance.GetType(), name).GetValue(instance)!;

    private static CardModel Card(CardModel card, CardPoolModel pool, string id)
    {
        // InitId also registers network serialization IDs, which requires the running game.
        AccessTools.Field(typeof(AbstractModel), "<Id>k__BackingField").SetValue(card, new ModelId("CARD", id));
        AccessTools.Field(typeof(CardModel), "_pool").SetValue(card, pool);
        return card;
    }

    private static object NewCatalog()
    {
        var catalog = RuntimeHelpers.GetUninitializedObject(CatalogType);
        foreach (var name in new[] { "_cardGroups", "_configuredCardGroups", "_pckCardOptions", "_providerInstanceIdentities" })
        {
            var field = AccessTools.Field(CatalogType, name);
            field.SetValue(catalog, Activator.CreateInstance(typeof(List<>).MakeGenericType(field.FieldType.GenericTypeArguments[0])));
        }
        return catalog;
    }
    private static object NewOption(string id, string[] cards)
    {
        var constructor = OptionType.GetConstructors().Single();
        var parameters = constructor.GetParameters();
        var args = parameters.Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray();
        args[0] = id; args[1] = id;
        args[2] = cards.ToDictionary(card => card, card => $"res://{id}/{card}.png", StringComparer.OrdinalIgnoreCase);
        args[3] = Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string),
            Mod.GetType("STS2SkinChanger.Catalog.AncientCardPortrait", true)!));
        return constructor.Invoke(args);
    }
    private static IList Groups(object catalog) => (IList)AccessTools.Field(CatalogType, "_cardGroups").GetValue(catalog)!;
    private static IList Options(object group) => (IList)AccessTools.Property(GroupType, "Options").GetValue(group)!;
    private static string Id(object group) => (string)AccessTools.Property(GroupType, "Id").GetValue(group)!;
    private static void AddGroup(object catalog, string id, params object[] options)
    {
        var group = Activator.CreateInstance(GroupType, [id, id])!;
        foreach (var option in options) Options(group).Add(option);
        Groups(catalog).Add(group);
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private sealed class ModdedPool : CardPoolModel
    {
        public override string Title => "modded";
        public override string EnergyColorName => "defect";
        public override string CardFrameMaterialPath => "card_frame_blue";
        public override Color DeckEntryCardColor => Colors.White;
        public override bool IsColorless => false;
        protected override CardModel[] GenerateAllCards() => [];
    }
}
