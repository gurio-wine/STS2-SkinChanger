using STS2SkinChanger.Catalog;
using STS2SkinChanger.Pck;

internal static class BaselinePartTakeoverTests
{
    private const string HandPath = "res://animations/backgrounds/merchant_room/hand/merchanthand.png";
    private const string RoomTopPath = "res://animations/backgrounds/merchant_room/top/shop_merchant_top.png";
    private static readonly byte[] BaselineHand = [1, 1, 1, 1];
    private static readonly byte[] StyleHand = [2, 2, 2, 2];

    // A style/UI package that is not a skin overrides one part of a presentation the game already
    // draws. It must stay a package the game loads itself, while that part becomes selectable and
    // the game original can be handed back. Everything here runs on synthetic packs: no game, no
    // provider initializer.
    internal static void Run()
    {
        var root = Directory.CreateTempSubdirectory("sc-part-takeover-");
        try
        {
            var game = Path.Combine(root.FullName, "game.pck");
            var style = Path.Combine(root.FullName, "style.pck");
            var uiOnly = Path.Combine(root.FullName, "uionly.pck");
            var anchored = Path.Combine(root.FullName, "anchored.pck");
            PckArchive.Write(game, new Dictionary<string, byte[]>
            {
                [HandPath] = BaselineHand,
                [RoomTopPath] = [7, 7]
            });
            PckArchive.Write(style, new Dictionary<string, byte[]>
            {
                [HandPath] = StyleHand,
                ["res://StyleLikeOverhaul/ui/panel.png"] = [3, 3, 3],
                ["res://StyleLikeOverhaul/audio/merchant/voice.wav"] = [4, 4]
            });
            PckArchive.Write(uiOnly, new Dictionary<string, byte[]>
            {
                ["res://UiOnlyOverhaul/ui/panel.png"] = [5, 5],
                ["res://UiOnlyOverhaul/fonts/ui.ttf"] = [6]
            });
            PckArchive.Write(anchored, new Dictionary<string, byte[]>
            {
                [RoomTopPath] = [8, 8]
            });

            var descriptors = new[]
            {
                new SkinModDescriptor("StyleLikeOverhaul", "Style Like Overhaul", style, false, root.FullName),
                new SkinModDescriptor("UiOnlyOverhaul", "UI Only Overhaul", uiOnly, false, root.FullName)
            };
            var probes = SkinCatalog.ProbeSkinProviders(descriptors, game)
                .ToDictionary(probe => probe.Id, StringComparer.OrdinalIgnoreCase);
            Require(!probes.ContainsKey("UiOnlyOverhaul"),
                "只提供界面、音频或字体且没有覆盖任何外观部件的包不能被当成皮肤来源。");
            var partProbe = probes["StyleLikeOverhaul"];
            Require(partProbe.IsBaselinePartTakeover &&
                    partProbe.VisualGroupCount == 0 &&
                    partProbe.BaselinePartGroupCount == 1 &&
                    partProbe.BaselinePartGroupIds!.SequenceEqual(["merchant"]),
                "只覆盖商人房间手部的风格包必须按部件接管处理，而不是被当成完整的商人皮肤。");
            var anchoredProbe = SkinCatalog.ProbeSkinProviders(
                    [new SkinModDescriptor("AnchoredMerchant", "Anchored Merchant", anchored, false, root.FullName)],
                    game)
                .Single();
            Require(!anchoredProbe.IsBaselinePartTakeover && anchoredProbe.VisualGroupCount == 1,
                "覆盖商人外观主体的包仍然是被隔离的皮肤提供者，不能降级成部件接管。");

            // The package keeps drawing its own shop scene, merchant model and shop interface. None
            // of that may be held back, handed back, repeated or removed by this Mod: doing so used
            // to turn the author's own merchant model and shop interface back into the game
            // original the moment the player selected that part.
            var modAssembly = typeof(STS2SkinChanger.Entry).Assembly;
            Require(
                modAssembly.GetType("STS2SkinChanger.Core.PartTakeoverScope") == null &&
                modAssembly.GetType("STS2SkinChanger.Core.PartTakeoverPresentation") == null &&
                modAssembly.GetType("STS2SkinChanger.Core.PartTakeoverVisualScope")
                    ?.GetMethod(
                        "ReleaseOwner",
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Static) == null,
                "部件接管只接管该包覆盖、且本体同路径存在的外观资源；作者自己画的商店场景与商店界面必须原样保留，不能被门控、交回、重放或删除。");

            using var catalog = SkinCatalog.Build(game, [descriptors[0]]);
            var merchant = catalog.Groups.Single(group => group.Id == "merchant");
            var part = merchant.Options.Single(option => option.Id == "StyleLikeOverhaul");
            Require(part.IsBaselinePartOverride && part.Assets.Keys.SequenceEqual([HandPath]),
                "部件选项只能包含该包覆盖、且本体同路径存在的那些外观资源。");
            Require(part.ProviderRootPath == root.FullName,
                "部件选项必须带上该包的目录：在线识别作者自带的商店画面与它自己的补丁时需要它。");
            Require(SkinCatalog.ResolvePartTakeoverDefault(merchant.Options) == "StyleLikeOverhaul",
                "部件接管原本就由作者的包生效，必须成为该分组的默认选择，识别它不能反过来关掉玩家看到的效果。");
            var shadow = catalog.BuildOverlay(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["merchant"] = SkinCatalog.BaseOptionId
            });
            Require(shadow.TryGetValue(HandPath, out var shadowFile) &&
                    shadowFile.Read().SequenceEqual(BaselineHand),
                "选择游戏原版时必须把该路径压回本体资源，否则作者的包全局挂载后手部依旧是他的版本。");
            var selected = catalog.BuildOverlay(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["merchant"] = "StyleLikeOverhaul"
            });
            Require(selected.TryGetValue(HandPath, out var selectedFile) &&
                    selectedFile.Read().SequenceEqual(StyleHand),
                "选择该部件时必须挂载作者自己的资源，玩家从别的选项切回来才能恢复。");
        }
        finally
        {
            root.Delete(true);
        }

        Console.WriteLine("Baseline part takeover passed: non-skin packages keep loading, only their overridden parts are selectable, and the game original is restorable.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
