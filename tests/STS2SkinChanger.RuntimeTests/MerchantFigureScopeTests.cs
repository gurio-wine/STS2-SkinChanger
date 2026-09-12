using STS2SkinChanger.Core;

internal static class MerchantFigureScopeTests
{
    // A style/UI package draws the merchant's figure from its own image and switches the game's
    // figure off while it does. The selection owns that figure, so any other choice has to bring the
    // game's figure back — hiding the package's figure alone left the shop with no merchant at all.
    internal static void Run()
    {
        Require(
            MerchantFigureScopePolicy.ResolveFigure(
                partSelected: true,
                packageFigureVisible: true,
                gameFigureVisibleAtRest: true,
                gameFigureVisible: true) == MerchantFigureScopePolicy.FigureAction.HideGameFigure,
            "选中该部件、且作者自己的商人确实画出来时，游戏原版商人才让位。");

        Require(
            MerchantFigureScopePolicy.ResolveFigure(true, false, true, true) ==
            MerchantFigureScopePolicy.FigureAction.Leave &&
            MerchantFigureScopePolicy.ResolveFigure(true, true, true, false) ==
            MerchantFigureScopePolicy.FigureAction.Leave,
            "作者没能画出自己的商人时不能把原版商人藏掉，否则商店里一个商人都没有。");

        Require(
            MerchantFigureScopePolicy.ResolveFigure(false, false, true, false) ==
            MerchantFigureScopePolicy.FigureAction.ShowGameFigure &&
            MerchantFigureScopePolicy.ResolveFigure(false, true, true, false) ==
            MerchantFigureScopePolicy.FigureAction.ShowGameFigure,
            "换成别的商人皮肤或游戏原版时，被作者关掉的原版商人画面必须回来。");

        Require(
            MerchantFigureScopePolicy.ResolveFigure(false, false, true, true) ==
            MerchantFigureScopePolicy.FigureAction.Leave &&
            MerchantFigureScopePolicy.ResolveFigure(false, false, false, false) ==
            MerchantFigureScopePolicy.FigureAction.Leave,
            "原版画面本来就正常显示、或本来就不该显示时，不做无谓改动。");

        Require(
            MerchantFigureScopePolicy.ShouldRestoreLayout(partSelected: false, layoutDiffersFromRest: true) &&
            !MerchantFigureScopePolicy.ShouldRestoreLayout(false, false) &&
            !MerchantFigureScopePolicy.ShouldRestoreLayout(true, true),
            "作者给商人按钮的位置只在别的选择生效时还原；选中它自己时保持作者排版。");

        // The room background and the shop interface stay with the package for every selection; only
        // the merchant's own figure and hand follow the merchant group.
        Require(
            MerchantAppearanceScopePolicy.IsMerchantAppearance(
                [("SpineSprite", "MerchantVisual"), ("NMerchantButton", "MerchantButton")]) &&
            MerchantAppearanceScopePolicy.IsMerchantAppearance(
                [("NMerchantHand", "MerchantHand"), ("NMerchantInventory", "Inventory")]) &&
            !MerchantAppearanceScopePolicy.IsMerchantAppearance(
                [("TextureRect", "CznStyleMerchantRoomBackground"), ("Control", "SceneContainer"),
                 ("NMerchantRoom", "MerchantRoom")]) &&
            !MerchantAppearanceScopePolicy.IsMerchantAppearance(
                [("TextureRect", "CznStyleMerchantItemFrame"), ("NMerchantRelic", "Relic"),
                 ("NMerchantInventory", "Inventory")]),
            "商人本体与手归商人选择；商店房间与商店界面不归它，任何选择下都保持作者原样。");

        Console.WriteLine(
            "Merchant figure scope passed: the package's merchant steps aside only for a figure it " +
            "really drew, and the game's own figure and layout return for every other selection.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
