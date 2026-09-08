using System.Runtime.CompilerServices;
using HarmonyLib;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Core;

internal static class MultiplayerSourceTests
{
    internal static void Run(bool handsOnly = false)
    {
        var previous = SkinService.Catalog;
        try
        {
            var catalog = CreateCatalog();
            AccessTools.Property(typeof(SkinService), "Catalog").SetValue(null, catalog);
            if (handsOnly)
            {
                foreach (var pose in new[] { "point", "rock", "paper", "scissors" })
                    Require(catalog.FindGroupIdForResourcePath(
                        $"res://images/ui/hands/multiplayer_hand_defect_{pose}.png") == "defect",
                        $"未预先登记的宝箱 {pose} 手部资源丢失角色归属。");
                Require(catalog.FindGroupIdForResourcePath("res://images/ui/hands/unrelated.png") == null,
                    "不能接管无关手部资源。");
                return;
            }

            // Same Workshop item, but only the sender has a second package with its manifest ID.
            Require(SkinService.GetAvailableCharacterSelectionSourceIds("defect", ["skin::source:123::variant:2"])
                .SequenceEqual(["skin::variant:2"]), "同一工坊差分因本机安装数量不同被当成未安装。");
            Require(SkinService.GetCharacterSelectionSourceIds("defect", "skin::variant:2")
                .SequenceEqual(["skin::source:123::variant:2"]), "发送来源必须保留稳定工坊 ID 和差分。");
            Require(SkinService.GetAvailableCharacterSelectionSourceIds("defect", ["skin::variant:2"])
                .SequenceEqual(["skin::variant:2"]), "无歧义的旧版本来源仍应可以接收。");
            Require(SkinService.GetAvailableCharacterSelectionSourceIds("defect", ["skin::source:456::variant:2"]).Count == 0,
                "相同 manifest ID 的另一个工坊物品不能冒充已安装来源。");
            Require(SkinService.GetAvailableCharacterSelectionSourceIds("ironclad", ["skin::source:123::variant:2"]).Count == 0,
                "来源不能跨角色串用。");

            catalog = CreateCatalog(duplicate: true);
            AccessTools.Property(typeof(SkinService), "Catalog").SetValue(null, catalog);
            Require(SkinService.GetAvailableCharacterSelectionSourceIds("defect", ["skin::source:123::variant:2"])
                .SequenceEqual(["skin::source:123::variant:2"]), "接收端多个同 ID 来源必须准确选择工坊物品。");
            Require(SkinService.GetAvailableCharacterSelectionSourceIds("defect", ["skin::variant:2"]).Count == 0,
                "旧端未提供工坊来源且本机有多个候选时不能猜测差分。");
            Require(SkinService.GetAvailableCharacterSelectionSourceIds("defect", ["skin::source:123::variant:9"]).Count == 0,
                "不存在的差分不能退成同来源的其它差分。");
            Require(SkinService.GetAvailableCharacterSelectionSourceIds("defect",
                    ["skin::source:456::variant:2", "skin::source:123::variant:2", "skin::source:456::variant:2"])
                .SequenceEqual(["skin::source:456::variant:2", "skin::source:123::variant:2"]),
                "合并皮肤必须保留发送者的来源优先级且不能重复来源。");
        }
        finally { AccessTools.Property(typeof(SkinService), "Catalog").SetValue(null, previous); }
        Console.WriteLine("Multiplayer source identity passed: Workshop ownership, variants, duplicate counts and ambiguity.");
    }

    private static SkinCatalog CreateCatalog(bool duplicate = false)
    {
        var catalog = (SkinCatalog)RuntimeHelpers.GetUninitializedObject(typeof(SkinCatalog));
        var group = new SkinGroup("defect", "defect");
        var providers = duplicate ? new[] { "skin::source:123", "skin::source:456" } : new[] { "skin" };
        group.Options.Add(new SkinOption("__base__", "base", new Dictionary<string, ResourceAsset>()));
        foreach (var provider in providers)
            group.Options.Add(new SkinOption(provider + "::variant:2", "skin", new Dictionary<string, ResourceAsset>(),
                IsRuntimeProvider: true, ProviderId: provider));
        Set("_groups", new List<SkinGroup> { group });
        Set("_characterAppearanceGroupIds", new HashSet<string> { "defect" });
        Set("_resourceGroupIds", new Dictionary<string, string>());
        Set("_providerInstanceIdentities", providers.Select(p => new ProviderInstanceIdentity("skin", p, "skin")).ToArray());
        Set("_workshopSourceIds", providers.Select((p, i) => (p, id: i == 0 ? 123UL : 456UL)).ToDictionary(x => x.p, x => x.id));
        return catalog;
        void Set(string name, object value) => AccessTools.Field(typeof(SkinCatalog), name).SetValue(catalog, value);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
