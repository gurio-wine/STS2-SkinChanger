using STS2SkinChanger.Core;
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
Require(
    open.NativeBackButtonHost == MerchantPreviewBackButtonHost.CompendiumOverlay,
    "打开商店后必须把原生返回键迁到主图鉴覆盖层，不能继续留在 SubViewport 内。");

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
Require(
    closed.NativeBackButtonHost == MerchantPreviewBackButtonHost.InventorySubViewport,
    "关闭商店后必须把原生返回键归还库存场景。");

var baselineFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["res://animations/backgrounds/merchant_room/hand/merchant_hand_skel_data.tres"] = "vanilla skeleton",
    ["res://animations/backgrounds/fake_merchant_room/hand/fake_merchant_hand_skel_data.tres"] = "vanilla fake skeleton",
    ["res://.godot/imported/merchanthand.skel-abc.spskel"] = "vanilla payload",
    ["res://unrelated/base_resource.tres"] = "unrelated"
};
var selectedProviderOverlayPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    // Only the real merchant group is selected from this multi-group provider.
    "res://animations/backgrounds/merchant_room/hand/merchant_hand_skel_data.tres.remap",
    "res://.godot/imported/merchanthand.skel-abc.spskel"
};
var removedPaths = PromotedPackOverlayPolicy.FindBaselinePathsShadowingSelectedRemaps(
    baselineFiles.Keys,
    selectedProviderOverlayPaths);
Require(
    removedPaths.Contains(
        "res://animations/backgrounds/merchant_room/hand/merchant_hand_skel_data.tres"),
    "当前选择通过 .remap 提供的资源不能被原版恢复包遮住。");
Require(
    !removedPaths.Contains(
        "res://animations/backgrounds/fake_merchant_room/hand/fake_merchant_hand_skel_data.tres"),
    "同一完整包中未选择的其它外观必须继续由原版恢复包隔离。");
Require(
    !removedPaths.Contains("res://.godot/imported/merchanthand.skel-abc.spskel"),
    "不带 .remap 的资源必须留给后续逐路径选择逻辑决定。");
Require(
    !removedPaths.Contains("res://unrelated/base_resource.tres"),
    "当前完整资源包未提供的资源仍必须由原版恢复包校正。");

Require(
    MerchantProviderReadyPolicy.ResolvePostfixTiming(MerchantProviderReadyTarget.Button) ==
    MerchantProviderPostfixTiming.NextFrameThenSpineReady,
    "商人按钮必须等下一帧的最终 Spine 就绪后再重放提供者 Postfix。");
Require(
    MerchantProviderReadyPolicy.ResolvePostfixTiming(MerchantProviderReadyTarget.Hand) ==
    MerchantProviderPostfixTiming.NextFrameThenSpineReady,
    "商人手部必须等下一帧的最终 Spine 就绪后再重放提供者 Postfix。");
Require(
    MerchantProviderReadyPolicy.ResolvePostfixTiming(MerchantProviderReadyTarget.Inventory) ==
    MerchantProviderPostfixTiming.Immediate,
    "不依赖 Spine 的库存节点应立即完成提供者 Postfix。");

var previewFocus = MerchantPreviewFocusState.None;
previewFocus = MerchantPreviewFocusPolicy.Resolve(
    previewFocus,
    MerchantPreviewFocusEvent.MouseEntered);
Require(previewFocus.IsFocused, "鼠标进入图鉴商人代理层时必须显示悬浮外观。");
previewFocus = MerchantPreviewFocusPolicy.Resolve(
    previewFocus,
    MerchantPreviewFocusEvent.ControllerFocused);
previewFocus = MerchantPreviewFocusPolicy.Resolve(
    previewFocus,
    MerchantPreviewFocusEvent.MouseExited);
Require(
    previewFocus.IsFocused,
    "鼠标离开但手柄焦点仍在图鉴商人代理层时不能提前取消悬浮外观。");
previewFocus = MerchantPreviewFocusPolicy.Resolve(
    previewFocus,
    MerchantPreviewFocusEvent.ControllerUnfocused);
Require(!previewFocus.IsFocused, "鼠标和手柄焦点都离开后必须恢复商人默认外观。");

Console.WriteLine("Merchant appearance policy tests passed.");
