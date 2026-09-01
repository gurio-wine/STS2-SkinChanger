using STS2SkinChanger.Ui;

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

var open = MerchantPreviewLayerPolicy.Resolve(
    inventoryOpen: true,
    hasSkinOptions: true,
    actionSelectorRequested: true,
    compendiumVisible: true);
Require(
    open.PreviewZIndex > MerchantPreviewLayerPolicy.HighestCompendiumOverlayZIndex,
    "打开商店后，整个原生商店视口必须高于图鉴全部叠加控件。");
Require(!open.SkinSelectorVisible, "打开商店后不应显示皮肤切换按钮。");
Require(!open.ActionSelectorVisible, "打开商店后不应显示图鉴动作按钮。");
Require(!open.CompendiumBackEnabled, "打开商店后图鉴返回键必须停用，由原生商店返回键接管。");

var closed = MerchantPreviewLayerPolicy.Resolve(
    inventoryOpen: false,
    hasSkinOptions: true,
    actionSelectorRequested: true,
    compendiumVisible: true);
Require(
    closed.PreviewZIndex == MerchantPreviewLayerPolicy.NormalPreviewZIndex,
    "关闭商店后必须恢复图鉴预览的正常层级。");
Require(closed.SkinSelectorVisible, "关闭商店后应恢复皮肤切换按钮。");
Require(closed.ActionSelectorVisible, "关闭商店后应恢复当前条目的动作按钮。");
Require(closed.CompendiumBackEnabled, "关闭商店后应恢复图鉴返回键。");

Console.WriteLine("Merchant preview layer policy tests passed.");
