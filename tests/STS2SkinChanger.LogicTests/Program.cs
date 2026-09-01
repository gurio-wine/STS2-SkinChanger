using STS2SkinChanger.Core;
using STS2SkinChanger.Ui;

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

Require(
    ManagedModListNamePolicy.Format("Treessa Silent Skin", isManagedProvider: true) ==
    "[SC] Treessa Silent Skin",
    "被 Skin Changer 识别为皮肤提供者的 Mod 必须只在 Mod 列表显示 [SC] 前缀。");
Require(
    ManagedModListNamePolicy.Format("More Action & Effects", isManagedProvider: false) ==
    "More Action & Effects",
    "未被接管的 Mod 名称必须保持原样。");
Require(
    ManagedModListNamePolicy.Format("[SC] Existing Prefix", isManagedProvider: true) ==
    "[SC] Existing Prefix",
    "Mod 列表反复刷新时不能重复叠加 [SC] 前缀。");

Require(
    CardPresentationLayoutPolicy.Resolve(
        isNativeAncient: false,
        requestsAncientLayout: false,
        requestsExpandedPortrait: true) == CardPresentationLayout.ExpandedPortrait,
    "普通异画借用先古大图层时只能进入扩展异画版式，不能被判成先古卡。");
Require(
    CardPresentationLayoutPolicy.Resolve(
        isNativeAncient: false,
        requestsAncientLayout: true,
        requestsExpandedPortrait: true) == CardPresentationLayout.Ancient,
    "皮肤明确声明先古版式时，先古意图必须高于扩展异画版式。");
Require(
    CardPresentationLayoutPolicy.Resolve(
        isNativeAncient: true,
        requestsAncientLayout: false,
        requestsExpandedPortrait: true) == CardPresentationLayout.Ancient,
    "游戏原生先古卡不能被扩展异画版式降级成普通卡。");

var providerAncientStyleMethod = AncientStyleMethodPolicy.Find(
    [typeof(ForeignCardProvider.ConfigHelper)]);
Require(
    providerAncientStyleMethod?.DeclaringType == typeof(ForeignCardProvider.ConfigHelper),
    "卡牌皮肤的先古样式开关必须按能力发现，不能限定为某个固定命名空间。");
Require(
    providerAncientStyleMethod?.Invoke(null, ["SovereignBlade"]) is true,
    "普通牌被提供者明确切换为先古异画时，必须读取到该提供者的卡图开关。");
Require(
    AncientStyleMethodPolicy.ResolveWithoutProviderMethod(
        isNativeAncient: false,
        requestsAncientLayout: true),
    "没有独立开关方法时，提供者明确声明的先古版式必须同时选择先古卡图。");
Require(
    !AncientStyleMethodPolicy.ResolveWithoutProviderMethod(
        isNativeAncient: false,
        requestsAncientLayout: false),
    "只同时导出普通图和先古图不能让所有普通牌误用先古卡图。");

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

var creatures = OtherCreatureCatalog.All;
Require(creatures.Count == 2, "其它图鉴必须登记异鸟宝宝和佩尔的士兵两个生物。");

var byrdpip = OtherCreatureCatalog.Find("Byrdpip") ??
               throw new InvalidOperationException("生物 ID 查找必须忽略大小写并识别异鸟宝宝。");
Require(
    byrdpip.ScenePath == "res://scenes/creature_visuals/byrdpip.tscn",
    "异鸟宝宝必须绑定游戏原生生物场景。");
Require(byrdpip.Actions.Count == 1, "异鸟宝宝只应公开实际使用的攻击动作。");
var byrdpipAttack = byrdpip.Actions.Single();
Require(
    byrdpipAttack.Kind == OtherCreatureActionKind.Attack,
    "异鸟宝宝的公开动作必须是攻击。");
Require(
    byrdpipAttack.SfxPath == OtherCreatureCatalog.ByrdpipAttackSfx,
    "异鸟宝宝攻击预览必须复用游戏原生攻击音效。");
Require(
    byrdpipAttack.FollowUpAliases.Contains("idle_loop", StringComparer.OrdinalIgnoreCase),
    "异鸟宝宝攻击后必须返回待机循环。");
Require(
    byrdpip.Actions.SelectMany(action => action.AnimationAliases)
        .All(alias => !alias.Contains("egg", StringComparison.OrdinalIgnoreCase) &&
                      !alias.Contains("ignore", StringComparison.OrdinalIgnoreCase)),
    "蛋状态和制作期废弃动作不得出现在异鸟宝宝动作列表中。");

var paelsLegion = OtherCreatureCatalog.Find("paels_legion") ??
                  throw new InvalidOperationException("其它图鉴必须登记佩尔的士兵。");
Require(
    paelsLegion.LocalizationTable == "relics" &&
    paelsLegion.LocalizationKey == "PAELS_LEGION.title",
    "佩尔的士兵必须使用游戏当前的遗物本地化名称。");
Require(
    paelsLegion.ScenePath == "res://scenes/creature_visuals/paels_legion.tscn",
    "佩尔的士兵必须绑定游戏原生生物场景。");
Require(
    paelsLegion.Actions.Select(action => action.Kind).SequenceEqual(
        new[]
        {
            OtherCreatureActionKind.Block,
            OtherCreatureActionKind.Sleep,
            OtherCreatureActionKind.Wake
        }),
    "佩尔的士兵必须按格挡、休眠、苏醒展示原版动作。");
Require(
    paelsLegion.Actions[0].FollowUpAliases.Contains(
        "block_loop",
        StringComparer.OrdinalIgnoreCase),
    "格挡动作必须衔接持续格挡循环。");
Require(
    paelsLegion.Actions[1].FollowUpAliases.Contains(
        "sleep_loop",
        StringComparer.OrdinalIgnoreCase),
    "休眠动作存在循环资源时必须衔接休眠循环。");
Require(
    paelsLegion.Actions[2].FollowUpAliases.Contains(
        "idle_loop",
        StringComparer.OrdinalIgnoreCase),
    "苏醒动作必须返回待机循环。");
Require(
    paelsLegion.Actions.All(action => action.SfxPath == null),
    "游戏没有为佩尔的士兵动作注册独立音效时，不应伪造音效。");

Console.WriteLine("Skin Changer logic policy tests passed.");

internal static class ForeignCardProvider
{
    internal static class ConfigHelper
    {
        internal static bool IsAncientStyleEnabled(string cardTypeName) =>
            cardTypeName.Equals("SovereignBlade", StringComparison.Ordinal);
    }
}
