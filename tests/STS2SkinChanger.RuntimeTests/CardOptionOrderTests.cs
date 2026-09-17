using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Cards;
using STS2SkinChanger;

/// <summary>
/// The individual-card dropdown ("follow category" mode) must list skins in the manual order
/// saved on the skin priority screen, not the catalogue's name order.
/// </summary>
internal static class CardOptionOrderTests
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
        try
        {
            var ordinary = Card(new StrikeDefect(), new DefectCardPool(), "STRIKE_DEFECT");
            var ancient = Card(new BiasedCognition(), new DefectCardPool(), "BIASED_COGNITION");

            void Configure(string json)
            {
                configProperty.SetValue(null, AccessTools.Method(configProperty.PropertyType, "Deserialize")
                    .Invoke(null, [json]));
                cacheField.SetValue(null, Activator.CreateInstance(cacheField.FieldType));
            }

            // Options are appended in name order, exactly like SkinCatalog.FinalizeCardGroups sorts
            // them, so any priority order proves the dropdown no longer follows the name order.
            var first = Option("skin:1", "StrikeDefect", "BiasedCognition");
            var second = Option("skin:2", "StrikeDefect", "BiasedCognition");
            var third = Option("skin:3", "StrikeDefect", "BiasedCognition");
            var fourth = Option("skin:4", "StrikeDefect", "BiasedCognition");
            var catalog = NewCatalog();
            AddGroup(catalog, "defect", first, second, third, fourth);
            AddGroup(catalog, "ancients", first, second, third, fourth);
            catalogProperty.SetValue(null, catalog);

            Configure("""
                {"CardSkinPriorities":{"defect":[
                {"OptionId":"skin:4","Enabled":true},{"OptionId":"skin:1","Enabled":true},
                {"OptionId":"skin:3","Enabled":false},{"OptionId":"skin:2","Enabled":true}]}}
                """);
            Require(Ids(GetCardOptions(ordinary)).SequenceEqual(["skin:1", "skin:2", "skin:3", "skin:4"]),
                "分类选项本身仍按名字序提供，本用例必须能区分两种顺序。");
            var ordered = Ids(GetCardOptionsInPriorityOrder(ordinary));
            Require(ordered.SequenceEqual(["skin:4", "skin:1", "skin:3", "skin:2"]),
                "单卡下拉必须跟随皮肤优先级界面的手动排序，当前为：" + string.Join(",", ordered) + "。");
            Require(ordered.Order(StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(Ids(GetCardOptions(ordinary)).Order(StringComparer.OrdinalIgnoreCase)),
                "跟随优先级只能改顺序，不能增删下拉里的皮肤条目。");
            Require(ordered.Contains("skin:3"),
                "被皮肤优先级界面关闭的皮肤仍须出现在单卡下拉里，本次只修顺序。");

            // A skin that the priority screen has never recorded must not displace the recorded
            // ones; it appends behind them instead of falling back to the name order.
            Options(Groups(catalog).Cast<object>().First(group => Id(group) == "defect"))
                .Add(Option("skin:5", "StrikeDefect", "BiasedCognition"));
            Configure("""
                {"CardSkinPriorities":{"defect":[
                {"OptionId":"skin:4","Enabled":true},{"OptionId":"skin:1","Enabled":true},
                {"OptionId":"skin:3","Enabled":false},{"OptionId":"skin:2","Enabled":true}]}}
                """);
            Require(Ids(GetCardOptionsInPriorityOrder(ordinary))
                    .SequenceEqual(["skin:4", "skin:1", "skin:3", "skin:2", "skin:5"]),
                "未记录进优先级的新皮肤必须追加在尾部，不能插进已有顺序。");

            // Ancient cards resolve their options from a single category. That category alone
            // decides the order; the other priority list must never be merged into it.
            Configure("""
                {"CardSkinPriorities":{
                "defect":[{"OptionId":"skin:2","Enabled":true},{"OptionId":"skin:1","Enabled":true},
                {"OptionId":"skin:4","Enabled":true},{"OptionId":"skin:3","Enabled":true}],
                "ancients":[{"OptionId":"skin:4","Enabled":true},{"OptionId":"skin:1","Enabled":true},
                {"OptionId":"skin:3","Enabled":true},{"OptionId":"skin:2","Enabled":true}]}}
                """);
            var ancientOrdered = Ids(GetCardOptionsInPriorityOrder(ancient));
            Require(ancientOrdered.SequenceEqual(["skin:4", "skin:1", "skin:3", "skin:2"]),
                "先古牌的下拉顺序必须取自实际提供选项的那个分类，当前为：" +
                string.Join(",", ancientOrdered) + "。");
            Require(ancientOrdered.Order(StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(Ids(GetCardOptions(ancient)).Order(StringComparer.OrdinalIgnoreCase)),
                "先古牌同样只能改顺序，集合必须与分类选项一致。");

            Console.WriteLine("Card option order passed: priority order, stable tail and single-category Ancient order.");
        }
        finally
        {
            configProperty.SetValue(null, oldConfig);
            catalogProperty.SetValue(null, oldCatalog);
            cacheField.SetValue(null, oldCache);
        }
    }

    private static object GetCardOptions(CardModel card) =>
        AccessTools.Method(Service, "GetCardOptions").Invoke(null, [card])!;

    private static object GetCardOptionsInPriorityOrder(CardModel card) =>
        AccessTools.Method(Service, "GetCardOptionsInPriorityOrder").Invoke(null, [card])!;

    private static string[] Ids(object options) => ((IEnumerable)options).Cast<object>()
        .Select(option => (string)AccessTools.Property(option.GetType(), "Id").GetValue(option)!)
        .ToArray();

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

    private static object Option(string id, params string[] cards) => NewOption(id, cards);

    private static object NewOption(string id, string[] cards)
    {
        var constructor = OptionType.GetConstructors().Single();
        var args = constructor.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray();
        args[0] = id;
        args[1] = id;
        args[2] = cards.ToDictionary(card => card, card => $"res://{id}/{card}.png", StringComparer.OrdinalIgnoreCase);
        args[3] = Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string),
            Mod.GetType("STS2SkinChanger.Catalog.AncientCardPortrait", true)!));
        return constructor.Invoke(args);
    }

    private static IList Groups(object catalog) =>
        (IList)AccessTools.Field(CatalogType, "_cardGroups").GetValue(catalog)!;

    private static IList Options(object group) =>
        (IList)AccessTools.Property(GroupType, "Options").GetValue(group)!;

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
}
