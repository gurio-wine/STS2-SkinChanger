using STS2SkinChanger.Core;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Ui;
using System.Reflection;
using System.Runtime.Loader;

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

var cardOverlayOwners = new CanonicalResourceOwnershipTracker();
string[] sharedCardDependencies =
[
    "res://images/atlases/card_atlas.sprites/silent/neutralize.tres.remap",
    "res://images/packed/card_portraits/silent/neutralize.png.import"
];
Require(
    cardOverlayOwners.RequiresActivation("silent-skin-a", sharedCardDependencies),
    "首次使用二进制卡图皮肤时必须激活它的规范依赖桥。");
cardOverlayOwners.MarkActivated("silent-skin-a", sharedCardDependencies);
Require(
    !cardOverlayOwners.RequiresActivation("silent-skin-a", sharedCardDependencies),
    "同一皮肤连续加载多张卡牌时不能反复重挂同一个资源包。");
cardOverlayOwners.MarkActivated("silent-skin-b", sharedCardDependencies);
Require(
    cardOverlayOwners.RequiresActivation("silent-skin-a", sharedCardDependencies),
    "切到另一套卡图后再切回时，旧皮肤必须重新取得共享依赖路径；" +
    "否则导出的 AtlasTexture 会继续显示后一套卡图或原图。");
cardOverlayOwners.MarkActivated(
    "token-skin",
    ["res://images/packed/card_portraits/token/shiv.png.import"]);
Require(
    cardOverlayOwners.RequiresActivation("silent-skin-a", sharedCardDependencies),
    "无关卡牌的资源包不能伪装成已恢复了被覆盖的猎手卡图依赖。");
cardOverlayOwners.MarkActivated("silent-skin-a", sharedCardDependencies);
cardOverlayOwners.MarkActivated(
    "token-skin",
    ["res://images/packed/card_portraits/token/shiv.png.import"]);
Require(
    !cardOverlayOwners.RequiresActivation("silent-skin-a", sharedCardDependencies),
    "挂载不相交的卡牌资源不应让已有皮肤重复重挂。");
cardOverlayOwners.Reset();
Require(
    cardOverlayOwners.RequiresActivation("silent-skin-a", sharedCardDependencies),
    "全局卡牌覆盖恢复原版后，所有隔离卡图必须在下次使用时重新取得依赖所有权。");

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
    CharacterGroupEvidencePolicy.ResolveEligibleGroups(
            ["regent", "defect"],
            ["regent"])
        .SetEquals(["regent"]),
    "一个完整储君皮肤即使捎带机器人头像，也不能出现在机器人皮肤列表；" +
    "弱头像证据不能创建第二个角色皮肤归属。");
Require(
    CharacterGroupEvidencePolicy.ResolveEligibleGroups(
            ["silent", "necrobinder"],
            [])
        .SetEquals(["silent", "necrobinder"]),
    "纯头像包没有模型锚点时必须保留它声明的全部角色，不能破坏独立头像 Mod。");
Require(
    CharacterGroupEvidencePolicy.ResolveEligibleGroups(
            ["regent", "defect"],
            ["regent", "defect"])
        .SetEquals(["regent", "defect"]),
    "真正同时提供两名角色完整模型的合集必须保留两个角色选项。");

Require(
    DirectCharacterRuntimeTargetPolicy.ResolveTargets(
            [("TargetCharacterId", "REGENT")],
            ["ironclad", "silent", "regent", "defect"])
        .SetEquals(["regent"]),
    "通过私有场景动态替换角色的 DLL 必须按明确的目标角色字段进入对应皮肤列表。");
Require(
    DirectCharacterRuntimeTargetPolicy.ResolveTargets(
            [("TargetCharacterId", "REGENT"), ("DefaultCharacterId", "DEFECT")],
            ["regent", "defect"])
        .SetEquals(["regent"]),
    "普通默认值或兼容字段中的其他角色 ID 不能把完整皮肤串进另一角色列表。");
Require(
    DirectCharacterRuntimeTargetPolicy.ResolveTargets(
            [("TargetCharacterIds", "REGENT"), ("TargetCharacterIds", "DEFECT")],
            ["regent", "defect"])
        .SetEquals(["regent", "defect"]),
    "明确声明多个目标角色的完整运行合集必须保留全部真实角色归属。");
Require(
    DirectCharacterRuntimeTargetPolicy.ResolveTargets(
            [("TargetCharacterId", "UNKNOWN_CHARACTER")],
            ["regent", "defect"])
        .Count == 0,
    "未由游戏或玩法 Mod 定义的角色 ID 不能生成幽灵皮肤分组。");
var directRuntimeFixtureAssembly = Assembly.GetExecutingAssembly().Location;
Require(
    DirectCharacterRuntimeTargetScanner.ScanAssembly(
            directRuntimeFixtureAssembly,
            ["regent", "defect"])
        .SetEquals(["regent"]),
    "完整运行皮肤扫描器必须能从实际 DLL 元数据读取目标角色常量，不能依赖 Mod 名称或私有资源目录名。");

var portraitCache = new BoundedLruCache<string, string>(2, StringComparer.OrdinalIgnoreCase);
Require(!portraitCache.Set("A", "portrait-a", out _), "未达到上限时不应逐出卡图缓存。");
Require(!portraitCache.Set("B", "portrait-b", out _), "刚好达到上限时不应逐出卡图缓存。");
Require(
    portraitCache.TryGetValue("A", out var touchedPortrait) && touchedPortrait == "portrait-a",
    "读取卡图缓存必须返回原值并刷新最近使用顺序。");
Require(
    portraitCache.Set("C", "portrait-c", out var evictedPortrait) &&
    evictedPortrait.Key == "B" &&
    evictedPortrait.Value == "portrait-b" &&
    portraitCache.ContainsKey("A") &&
    portraitCache.ContainsKey("C") &&
    !portraitCache.ContainsKey("B"),
    "卡图缓存达到上限后必须只逐出最久未使用项，避免浏览多个分类后 GPU 资源无限累积。");

Require(
    AliasedDependencyCachePolicy.CanReuseExternalDependencies(
        [new AliasedDependencyReference(true, ["res://images/card_atlas.png"])],
        ["res://images/card_atlas.png"]),
    "文本卡牌资源的全部依赖都已复制到唯一别名时，应复用同一图集而不是为每张牌深度重载。");
Require(
    !AliasedDependencyCachePolicy.CanReuseExternalDependencies(
        [new AliasedDependencyReference(false, ["res://images/card_atlas.png"])],
        ["res://images/card_atlas.png"]),
    "仍在二进制资源中引用公共路径时必须保持深度隔离，避免不同皮肤串用缓存。");
Require(
    !AliasedDependencyCachePolicy.CanReuseExternalDependencies(
        [new AliasedDependencyReference(true, ["res://images/unmapped_atlas.png"])],
        ["res://images/card_atlas.png"]),
    "存在未复制的公共依赖时不能启用依赖复用。");

var iconOptions = new HashSet<string>(["SilentIcons"], StringComparer.OrdinalIgnoreCase);
Require(
    CharacterIconSelectionPolicy.ResolveResourceSelection(
        CharacterIconSelectionPolicy.FollowCharacterSkinOptionId,
        "TreessaSkin",
        "__base__",
        iconOptions,
        configuredSourceContainsResource: false) == "TreessaSkin",
    "头像设为跟随皮肤时必须使用当前角色皮肤的头像。");
Require(
    CharacterIconSelectionPolicy.ResolveResourceSelection(
        "SilentIcons",
        "TreessaSkin",
        "__base__",
        iconOptions,
        configuredSourceContainsResource: true) == "SilentIcons",
    "独立头像包提供当前资源时必须优先于整套角色皮肤。");
Require(
    CharacterIconSelectionPolicy.ResolveResourceSelection(
        "SilentIcons",
        "TreessaSkin",
        "__base__",
        iconOptions,
        configuredSourceContainsResource: false) == "TreessaSkin",
    "独立头像包没有覆盖某类头像时应回退到当前角色皮肤，不能丢失资源。");
Require(
    CharacterIconSelectionPolicy.ResolveResourceSelection(
        "__base__",
        "TreessaSkin",
        "__base__",
        iconOptions,
        configuredSourceContainsResource: false) == "__base__",
    "玩家明确选择游戏原版头像时不得再回退到角色皮肤。");
Require(
    CharacterIconSelectionPolicy.ResolveResourceSelection(
        "RemovedIconPack",
        "TreessaSkin",
        "__base__",
        iconOptions,
        configuredSourceContainsResource: true) == "TreessaSkin",
    "已卸载的头像来源必须安全回退到跟随皮肤。");

var persistentVisualSelections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["watcher"] = "WatcherSkinA",
    ["ironclad"] = "IroncladSkinA"
};
var previewVisualSelections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["watcher"] = "WatcherSkinB"
};
var remoteVisualSelections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["watcher"] = "RemoteWatcherSkin"
};
var previewMergedSelections = VisualSelectionOverlayPolicy.Merge(
    persistentVisualSelections,
    previewVisualSelections,
    scopedSelections: null);
Require(
    previewMergedSelections["watcher"] == "WatcherSkinB" &&
    previewMergedSelections["ironclad"] == "IroncladSkinA" &&
    persistentVisualSelections["watcher"] == "WatcherSkinA",
    "选角悬浮预览必须临时覆盖当前角色，又不能改写玩家已经保存的皮肤选择。");
var scopedMergedSelections = VisualSelectionOverlayPolicy.Merge(
    persistentVisualSelections,
    previewVisualSelections,
    remoteVisualSelections);
Require(
    scopedMergedSelections["watcher"] == "RemoteWatcherSkin",
    "联机玩家的实例化作用域必须高于本机选角悬浮预览，不能把预览皮肤串给其他玩家。");
Require(
    VisualSelectionOverlayPolicy.AffectedGroups(
            ["watcher", "watcher:relic"],
            ["watcher", "watcher:portrait"])
        .SetEquals(["watcher", "watcher:relic", "watcher:portrait"]),
    "切换或关闭悬浮预览时必须同时恢复上一预览和下一预览涉及的全部外观分组。");

Require(
    PauseMenuAppearanceEntryPolicy.Resolve(showEntry: false, buttonExists: true) ==
    new PauseMenuAppearanceEntryDecision(CreateButton: false, ShowButton: false),
    "玩家关闭局内外观入口后，已经建立的暂停菜单按钮也必须隐藏。");
Require(
    PauseMenuAppearanceEntryPolicy.Resolve(showEntry: true, buttonExists: false) ==
    new PauseMenuAppearanceEntryDecision(CreateButton: true, ShowButton: true),
    "玩家重新启用局内外观入口后，下一次打开暂停菜单必须重新建立按钮。");

var infoPanelPlacement = CharacterSelectorPlacementPolicy.Resolve(useTopRight: false);
Require(
    infoPanelPlacement.Host == CharacterSelectorHost.InfoPanel &&
    infoPanelPlacement.AnchorLeft == 0.5f &&
    infoPanelPlacement.AnchorRight == 0.5f,
    "默认选角皮肤控件必须继续锚定在角色信息框上方，不能因新增位置选项改变旧布局。");
var topRightPlacement = CharacterSelectorPlacementPolicy.Resolve(useTopRight: true);
Require(
    topRightPlacement.Host == CharacterSelectorHost.Screen &&
    topRightPlacement.AnchorLeft == 1f &&
    topRightPlacement.AnchorRight == 1f &&
    topRightPlacement.OffsetRight < 0f &&
    topRightPlacement.OffsetTop > 77f,
    "右上角布局必须相对整个选角屏幕锚定，并避开原版右上角的章节选择器。");

var defaultCardSelectorPosition = CardSkinSelectorPlacementPolicy.ResolveStored(
    storedX: null,
    storedY: null);
Require(
    defaultCardSelectorPosition == CardSkinSelectorPlacementPolicy.DefaultPosition,
    "未移动过单卡皮肤控件时必须保持原来的顶部居中位置。");
var clampedCardSelectorPosition = CardSkinSelectorPlacementPolicy.ClampNormalized(
    requestedX: -2f,
    requestedY: 3f,
    viewportWidth: 2560f,
    viewportHeight: 1200f);
Require(
    clampedCardSelectorPosition.X >=
        CardSkinSelectorPlacementPolicy.SelectorWidth / 2f / 2560f &&
    clampedCardSelectorPosition.Y <=
        1f - CardSkinSelectorPlacementPolicy.SelectorHeight / 2f / 1200f,
    "拖动单卡皮肤控件时必须限制在屏幕内，避免保存后再也无法找回。");

var singleProviderIdentity = ProviderInstanceIdentityPolicy.Resolve(
    [new ProviderInstanceCandidate("Same.Id", "Single Skin", @"D:\mods\only")]);
Require(
    singleProviderIdentity.Single().InstanceId == "Same.Id" &&
    singleProviderIdentity.Single().DisplayName == "Single Skin",
    "没有重复 Mod ID 时必须保留旧选项 ID 和名称，避免破坏玩家已有设置。");
var duplicateProviderIdentities = ProviderInstanceIdentityPolicy.Resolve(
    [
        new ProviderInstanceCandidate(
            "Same.Id",
            "Shared Skin",
            @"D:\Steam\steamapps\workshop\content\2868840\1111111111"),
        new ProviderInstanceCandidate(
            "same.id",
            "Shared Skin",
            @"D:\Steam\steamapps\workshop\content\2868840\2222222222")
    ]);
Require(
    duplicateProviderIdentities.Select(identity => identity.InstanceId)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count() == 2 &&
    duplicateProviderIdentities.All(identity =>
        identity.InstanceId.StartsWith("Same.Id::source:", StringComparison.OrdinalIgnoreCase)),
    "相同 Mod ID 的两个来源必须得到稳定且互不冲突的内部身份。");
Require(
    duplicateProviderIdentities.Select(identity => identity.DisplayName)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count() == 2,
    "相同 Mod ID 且同名的差分包必须在皮肤列表里可区分。");
Require(
    duplicateProviderIdentities[1].ManifestId == "same.id",
    "实例 ID 可统一大小写，但资源命名空间必须保留每个包清单中的原始大小写。");
var firstDuplicateInstanceId = duplicateProviderIdentities[0].InstanceId;
Require(
    ProviderInstanceIdentityPolicy.ScopeOptionId(
        "Same.Id",
        firstDuplicateInstanceId,
        "custom.skin") == firstDuplicateInstanceId + "::option:custom.skin",
    "框架自行命名的选项也必须进入对应差分包的独立作用域。");
Require(
    ProviderInstanceIdentityPolicy.IsOptionSelectionAlias(
        "Same.Id",
        firstDuplicateInstanceId,
        firstDuplicateInstanceId,
        "Same.Id"),
    "旧版按 Mod ID 保存的选择必须迁移到第一个同 ID 差分包。");
Require(
    ProviderInstanceIdentityPolicy.IsOptionSelectionAlias(
        "Same.Id",
        firstDuplicateInstanceId,
        firstDuplicateInstanceId + "::variant:painted",
        "Same.Id::variant:painted"),
    "旧版带变体后缀的选择必须保留到对应差分包变体。");
Require(
    ProviderInstanceIdentityPolicy.IsOptionSelectionAlias(
        "Same.Id",
        firstDuplicateInstanceId,
        firstDuplicateInstanceId + "::option:custom.skin",
        "custom.skin"),
    "框架自行命名的旧皮肤选项必须能迁移到差分包作用域。");
Require(
    ProviderInstanceIdentityPolicy.IsOptionSelectionAlias(
        "Same.Id",
        "Same.Id",
        "Same.Id::variant:painted",
        firstDuplicateInstanceId + "::variant:painted"),
    "卸载同 ID 差分包后，剩余单包必须能接回之前保存的实例化选择。");

var disabledMultiplayerSync = MultiplayerSkinSyncParticipationPolicy.Resolve(
    enabled: false,
    isMultiplayer: true);
Require(
    !disabledMultiplayerSync.AttachTransport &&
    !disabledMultiplayerSync.WriteCapabilityTrailer &&
    !disabledMultiplayerSync.ReadCapabilityTrailer &&
    !disabledMultiplayerSync.ApplyRemoteAppearance,
    "关闭联机皮肤同步后必须彻底停止监听、能力探测和远端外观覆盖；" +
    "不能只隐藏选项却继续向未安装 Skin Changer 的玩家修改握手包。");
var enabledMultiplayerSync = MultiplayerSkinSyncParticipationPolicy.Resolve(
    enabled: true,
    isMultiplayer: true);
Require(
    enabledMultiplayerSync.AttachTransport &&
    enabledMultiplayerSync.WriteCapabilityTrailer &&
    enabledMultiplayerSync.ReadCapabilityTrailer &&
    enabledMultiplayerSync.ApplyRemoteAppearance,
    "默认启用联机皮肤同步时必须保留现有的同装皮肤、头像和参数同步流程。");
var singlePlayerSync = MultiplayerSkinSyncParticipationPolicy.Resolve(
    enabled: true,
    isMultiplayer: false);
Require(
    !singlePlayerSync.AttachTransport &&
    !singlePlayerSync.WriteCapabilityTrailer &&
    !singlePlayerSync.ReadCapabilityTrailer &&
    !singlePlayerSync.ApplyRemoteAppearance,
    "单人游戏不能因为总开关默认开启而启动任何联机皮肤网络路径。");

var offscreenTransform = new CharacterCombatTransform(5f, 1800f, -900f)
{
    HealthBarScale = 1.35f,
    HealthBarOffsetX = 14f,
    HealthBarOffsetY = -22f,
    HealthBarFollowsModelMovement = false,
    IntentScale = 0.8f,
    IntentOffsetX = 31f,
    SelectionReticleScale = 1.6f,
    SelectionReticleOffsetY = 42f
};
Require(
    CharacterTransformResetPolicy.NeedsModelReset(offscreenTransform),
    "角色模型被缩放或移出屏幕时必须显示无需点中模型的恢复入口。");
var restoredTransform = CharacterTransformResetPolicy.ResetModel(offscreenTransform);
Require(
    restoredTransform.Scale == 1f &&
    restoredTransform.OffsetX == 0f &&
    restoredTransform.OffsetY == 0f,
    "恢复入口必须把模型大小和位置恢复到默认值。");
Require(
    restoredTransform.HealthBarScale == offscreenTransform.HealthBarScale &&
    restoredTransform.HealthBarOffsetX == offscreenTransform.HealthBarOffsetX &&
    restoredTransform.HealthBarOffsetY == offscreenTransform.HealthBarOffsetY &&
    restoredTransform.HealthBarFollowsModelMovement ==
        offscreenTransform.HealthBarFollowsModelMovement &&
    restoredTransform.IntentScale == offscreenTransform.IntentScale &&
    restoredTransform.IntentOffsetX == offscreenTransform.IntentOffsetX &&
    restoredTransform.SelectionReticleScale == offscreenTransform.SelectionReticleScale &&
    restoredTransform.SelectionReticleOffsetY == offscreenTransform.SelectionReticleOffsetY,
    "恢复模型位置不能顺带覆盖玩家单独设置的血条、意图或选择框。");
Require(
    !CharacterTransformResetPolicy.NeedsModelReset(restoredTransform),
    "模型恢复后不应继续显示紧急恢复入口。");
Require(
    ManagedProviderDisplayPolicy.IsManaged(
        "KaguyaSilentRavenSkin",
        @"D:\\Steam\\workshop\\3786286239",
        [@"D:\\Formal\\mods\\KaguyaSilentRavenSkin"],
        ["KaguyaSilentRavenSkin"]),
    "同一皮肤 Mod 的正式版本地快照与 Steam 副本路径不同，也必须都按清单 ID 显示 [SC]；" +
    "不能只依赖被扫描到的那一个根目录。");
Require(
    !ManagedProviderDisplayPolicy.IsManaged(
        "MoreActionEffects",
        @"D:\\Steam\\workshop\\utility",
        [@"D:\\Formal\\mods\\KaguyaSilentRavenSkin"],
        ["KaguyaSilentRavenSkin"]),
    "不同 ID、不同来源的普通功能 Mod 不能因为正式版存在本地皮肤快照而误标 [SC]。");

var managedCharacterAssetProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "ATA_IronClad",
    "ChizuruIroncladSkin"
};
Require(
    ManagedCharacterAssetRegistrationPolicy.ShouldSuppress(
        "ata_ironclad",
        managedCharacterAssetProviders),
    "已由 Skin Changer 接管的皮肤不能继续把角色场景写入 RitsuLib 全局注册表；" +
    "否则切走后商店、营火与战斗仍会加载上一个皮肤的私有路径。");
Require(
    !ManagedCharacterAssetRegistrationPolicy.ShouldSuppress(
        "CznStyleUI",
        managedCharacterAssetProviders),
    "普通 UI 或玩法 Mod 的 RitsuLib 注册不能被皮肤接管策略误拦截。");

Require(
    CharacterCombatSceneInstantiationPolicy.ShouldUseManagedFactory(
        isBaseSelection: false,
        hasManagedCombatScene: true,
        hasManagedCombatDependencies: false),
    "局内热切换到任何包含战斗场景的角色皮肤时，都必须先通过兼容场景工厂实例化；" +
    "不能只处理带框架契约的皮肤，否则普通资源型与完整 DLL 皮肤会把 Node2D 强转失败。");
Require(
    CharacterCombatSceneInstantiationPolicy.ShouldUseManagedFactory(
        isBaseSelection: true,
        hasManagedCombatScene: false,
        hasManagedCombatDependencies: false),
    "切回游戏原皮也必须从隔离的原版场景和依赖重建，不能复用上一皮肤占用的 Godot 缓存。");
Require(
    CharacterCombatSceneInstantiationPolicy.ShouldUseManagedFactory(
        isBaseSelection: false,
        hasManagedCombatScene: false,
        hasManagedCombatDependencies: true),
    "只替换规范骨骼等战斗依赖的皮肤也必须走隔离实例化；否则提供者用 CacheMode.Reuse 时会读到上一皮肤的骨骼。");
Require(
    CharacterCombatSceneInstantiationPolicy.HasManagedCombatDependencies(
        "res://scenes/creature_visuals/ironclad.tscn",
        ["res://animations/characters/ironclad/ironclad_skel_data.tres"]),
    "千鹤这类不替换根场景、只替换铁甲战士规范骨骼的皮肤必须被识别为战斗模型皮肤。");
Require(
    !CharacterCombatSceneInstantiationPolicy.HasManagedCombatDependencies(
        "res://scenes/creature_visuals/ironclad.tscn",
        ["res://animations/characters/silent/silent_skel_data.tres"]),
    "不能把另一名角色的骨骼依赖误判成当前角色的战斗模型资源。");
Require(
    !CharacterCombatSceneInstantiationPolicy.ShouldUseManagedFactory(
        isBaseSelection: false,
        hasManagedCombatScene: false,
        hasManagedCombatDependencies: false),
    "只修改名称、头像或其它素材而没有战斗场景的皮肤不能拦截角色模型创建。");
Require(
    CharacterCombatSceneInstantiationPolicy.ShouldRestoreCanonicalOwnership(
        scopedSelection: "remote-skin",
        configuredSelection: "local-skin"),
    "为另一名玩家临时实例化皮肤后，必须恢复本机选择对规范资源缓存的所有权。");
Require(
    !CharacterCombatSceneInstantiationPolicy.ShouldRestoreCanonicalOwnership(
        scopedSelection: null,
        configuredSelection: "local-skin"),
    "本机普通热切换完成后应保留当前选择的规范资源缓存所有权，供后续动画回调使用。");
Require(
    !CharacterCombatSceneInstantiationPolicy.ShouldRestoreCanonicalOwnership(
        scopedSelection: "local-skin",
        configuredSelection: "local-skin"),
    "联机作用域与本机配置相同时仍属于本机选择，不能恢复成旧皮肤缓存。");

var runtimeProviderCandidates = new RuntimeProviderCandidate[]
{
    new("MeirinWatcherSkin", ["watcher"], IsRunWideMonsterProvider: false),
    new("Merchant2CuteII", ["merchant"], IsRunWideMonsterProvider: false),
    new("AncientWaifus_Beta", ["neow", "pael"], IsRunWideMonsterProvider: false),
    new("CznEnemySkin", ["twig_slime_s", "twig_slime_m"], IsRunWideMonsterProvider: true)
};
Require(
    RuntimeProviderScopePolicy.SelectActiveProviders(
            runtimeProviderCandidates,
            scope: null)
        .SetEquals(runtimeProviderCandidates.Select(candidate => candidate.ProviderId)),
    "游戏资源初始化前尚未建立可见范围时，必须先完成所有已选完整皮肤的一次性初始化；" +
    "否则延迟加载的自定义场景会被缓存成没有脚本类型的普通 Node2D。");
Require(
    RuntimeProviderScopePolicy.SelectActiveProviders(
        runtimeProviderCandidates,
        new RuntimeProviderScope([], RunEnvironmentProviderIds: [])).Count == 0,
    "启动阶段没有可见外观分组时，不得提前执行任何第三方皮肤初始化器。");
Require(
    RuntimeProviderScopePolicy.SelectActiveProviders(
            runtimeProviderCandidates,
            new RuntimeProviderScope(["WATCHER"], RunEnvironmentProviderIds: []))
        .SetEquals(["MeirinWatcherSkin"]),
    "选角界面只能激活当前预览角色，不能同时保留商人、先古或怪物皮肤代码。");
Require(
    RuntimeProviderScopePolicy.SelectActiveProviders(
            runtimeProviderCandidates,
            new RuntimeProviderScope(["pael"], RunEnvironmentProviderIds: []))
        .SetEquals(["AncientWaifus_Beta"]),
    "其它图鉴只应激活当前预览实体所属的交互提供者。");
Require(
    RuntimeProviderScopePolicy.SelectActiveProviders(
            runtimeProviderCandidates,
            new RuntimeProviderScope(["watcher"], RunEnvironmentProviderIds: []))
        .SetEquals(["MeirinWatcherSkin"]),
    "进入对局不能因为某个怪物提供者拥有整局行为，就无条件启用它的地图、背景和音乐；" +
    "这些环境行为必须由当前地区的皮肤优先级另行授权。");
Require(
    RuntimeProviderScopePolicy.SelectActiveProviders(
            runtimeProviderCandidates,
            new RuntimeProviderScope(
                ["watcher"],
                RunEnvironmentProviderIds: ["cznenemyskin"]))
        .SetEquals(["MeirinWatcherSkin", "CznEnemySkin"]),
    "当前地区优先级明确选中的整局怪物提供者应获得地图、背景和音乐环境权。");
Require(
    RuntimeProviderScopePolicy.SelectActiveProviders(
            runtimeProviderCandidates,
            new RuntimeProviderScope(
                ["watcher"],
                RunEnvironmentProviderIds: ["Merchant2CuteII"]))
        .SetEquals(["MeirinWatcherSkin"]),
    "地区环境授权不得误激活不具备整局怪物环境能力的角色或商人提供者。");

var environmentPriorityCandidates = new RuntimeProviderPriorityCandidate[]
{
    new("DisabledMonsterPack", Enabled: false, IsRunWideMonsterProvider: true),
    new("TextureOnlyPack", Enabled: true, IsRunWideMonsterProvider: false),
    new(
        "InactiveEnvironmentPack",
        Enabled: true,
        IsRunWideMonsterProvider: true,
        AppliesToCurrentCombat: false),
    new("CznEnemySkin", Enabled: true, IsRunWideMonsterProvider: true),
    new("LaterEnvironmentPack", Enabled: true, IsRunWideMonsterProvider: true)
};
Require(
    RuntimeProviderScopePolicy.SelectRunEnvironmentProviders(environmentPriorityCandidates)
        .SetEquals(["CznEnemySkin"]),
    "地区环境只能采用优先级最高且已启用的整局怪物提供者，不能叠加多个背景或音乐来源。");
Require(
    RuntimeProviderScopePolicy.SelectRunEnvironmentProviders(
            [
                new RuntimeProviderPriorityCandidate(
                    "InactiveEnvironmentPack",
                    Enabled: true,
                    IsRunWideMonsterProvider: true,
                    AppliesToCurrentCombat: false)
            ])
        .Count == 0,
    "战斗中的背景与 BGM 不能采用本场没有任何怪物实际选择的整局皮肤提供者。");
Require(
    RuntimeProviderScopePolicy.MergeVisibleGroups(
            ["changed_monster"],
            ["other_live_monster", "current_player"])
        .SetEquals(["changed_monster", "other_live_monster", "current_player"]),
    "局内只修改一只怪物时仍必须保留本场其它怪物和玩家的运行期提供者。");
Require(
    RunEnvironmentRefreshPolicy.SelectMusicMode(
        isCombatInProgress: true,
        hasCombatState: true) == RunEnvironmentMusicMode.Combat,
    "战斗中切换怪物或地区优先级必须恢复本场战斗 BGM，不能误播地区地图音乐。");
Require(
    RunEnvironmentRefreshPolicy.SelectMusicMode(
        isCombatInProgress: false,
        hasCombatState: true) == RunEnvironmentMusicMode.Map,
    "战斗已经结束时不能因仍可读取旧 CombatState 而继续播放战斗 BGM。");
Require(
    RuntimeProviderScopePolicy.IsRunEnvironmentPatchTarget(
        "EncounterModel",
        "CreateBackground") &&
    RuntimeProviderScopePolicy.IsRunEnvironmentPatchTarget(
        "NRunMusicController",
        "UpdateMusic"),
    "战斗背景与局内音乐补丁必须归入地区环境行为，不能随单只怪物皮肤一起启用。");
Require(
    !RuntimeProviderScopePolicy.IsRunEnvironmentPatchTarget(
        "NBossMapPoint",
        "_Ready") &&
    !RuntimeProviderScopePolicy.IsRunEnvironmentPatchTarget(
        "MonsterModel",
        "CreateVisuals"),
    "Boss 图标和怪物模型仍属于具体怪物皮肤，不能被地区环境开关一并屏蔽。");
Require(
    RuntimeProviderScopePolicy.IsRunEnvironmentCallback(
        "RunMusicRuntime",
        "OnCombatBegan") &&
    RuntimeProviderScopePolicy.IsRunEnvironmentCallback(
        "RunMusicRuntime",
        "SetBossMusicPhase") &&
    !RuntimeProviderScopePolicy.IsRunEnvironmentCallback(
        "EnemyActionPresentationRuntime",
        "OnMoveStarted"),
    "提供者直接订阅的音乐回调必须受地区环境授权保护，而怪物动作回调不能被误伤。");

var scopedMonsterSnapshotType = typeof(RuntimeProviderScopePolicy).Assembly.GetType(
    "STS2SkinChanger.Core.ScopedMonsterSelectionSnapshot");
Require(
    scopedMonsterSnapshotType != null,
    "逐怪物皮肤启用判断必须使用可原子替换的只读快照，不能在 CZN 的高频判断入口里反射、加全局锁并重新查询目录。");
var scopedMonsterSnapshot = Activator.CreateInstance(scopedMonsterSnapshotType!);
var replaceScopedMonsterSelections = scopedMonsterSnapshotType!.GetMethod("Replace");
var isScopedMonsterSelectedMethod = scopedMonsterSnapshotType.GetMethod("IsSelected");
Require(
    scopedMonsterSnapshot != null &&
    replaceScopedMonsterSelections != null &&
    isScopedMonsterSelectedMethod != null,
    "逐怪物选择快照必须提供 Replace 与 IsSelected 行为。");
replaceScopedMonsterSelections!.Invoke(scopedMonsterSnapshot, [
    new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase)
    {
        ["CznEnemySkin"] = ["SEAPUNK", "CORPSE_SLUG"]
    }
]);
var isScopedMonsterSelected = (Func<string, string, bool>)isScopedMonsterSelectedMethod!
    .CreateDelegate(typeof(Func<string, string, bool>), scopedMonsterSnapshot);
Require(
    isScopedMonsterSelected("cznenemyskin", "seapunk"),
    "逐怪物选择快照必须按不区分大小写的 Mod 与怪物 ID 路由 CZN 皮肤。");
replaceScopedMonsterSelections.Invoke(scopedMonsterSnapshot, [
    new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase)
    {
        ["CznEnemySkin"] = ["SEWER_CLAM"]
    }
]);
Require(
    !isScopedMonsterSelected("CznEnemySkin", "SEAPUNK") &&
    isScopedMonsterSelected("CznEnemySkin", "sewer_clam"),
    "替换逐怪物选择快照时必须同时移除旧选择，不能让上一场战斗的 CZN 路由残留。");

var scopedMonsterRoutePolicyType = typeof(RuntimeProviderScopePolicy).Assembly.GetType(
    "STS2SkinChanger.Core.ScopedMonsterRoutePolicy");
var createMonsterIdAccessor = scopedMonsterRoutePolicyType?.GetMethod(
    "CreateMonsterIdAccessor",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
Require(
    createMonsterIdAccessor != null,
    "逐怪物路由必须为提供者的 Profile 类型预编译 MonsterId 读取器，不能在每次判断时重复反射属性。");
var monsterIdAccessor = (Func<object, string?>)createMonsterIdAccessor!.Invoke(
    null,
    [typeof(ScopedMonsterProfileFixture)])!;
var scopedMonsterProfile = new ScopedMonsterProfileFixture("PHANTASMAL_GARDENER");
Require(
    monsterIdAccessor(scopedMonsterProfile) == "PHANTASMAL_GARDENER",
    "预编译的逐怪物路由必须读取 Profile.Target.MonsterId。");
var nonPublicMonsterIdAccessor = (Func<object, string?>)createMonsterIdAccessor.Invoke(
    null,
    [typeof(NonPublicScopedMonsterProfileFixture)])!;
Require(
    nonPublicMonsterIdAccessor(new NonPublicScopedMonsterProfileFixture("TORCH_HEAD_AMALGAM")) ==
    "TORCH_HEAD_AMALGAM",
    "原提供者以非公开属性保存 Target/MonsterId 时，预编译路由也必须保持兼容。");
_ = monsterIdAccessor(scopedMonsterProfile);
_ = isScopedMonsterSelected("CznEnemySkin", "SEWER_CLAM");
var scopedRouteAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
for (var index = 0; index < 50_000; index++)
{
    _ = monsterIdAccessor(scopedMonsterProfile);
    _ = isScopedMonsterSelected("CznEnemySkin", "SEWER_CLAM");
}
var scopedRouteAllocated = GC.GetAllocatedBytesForCurrentThread() - scopedRouteAllocatedBefore;
Require(
    scopedRouteAllocated <= 1024,
    $"逐怪物热路径执行 50,000 次只允许产生极少量运行时分配，实际为 {scopedRouteAllocated} 字节。");

var runtimeResourceRetentionPolicyType = typeof(RuntimeProviderScopePolicy).Assembly.GetType(
    "STS2SkinChanger.Core.RuntimeResourceRetentionPolicy");
var selectTransientCombatGroups = runtimeResourceRetentionPolicyType?.GetMethod(
    "SelectTransientCombatGroups",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
Require(
    selectTransientCombatGroups != null,
    "战斗资源缓存必须区分整局角色与当前房间怪物，离开房间后不能继续强引用所有遇到过的 CZN 场景和纹理。");
var transientCombatGroups = (IReadOnlySet<string>)selectTransientCombatGroups!.Invoke(
    null,
    [
        new[] { "ironclad", "SEAPUNK", "CORPSE_SLUG" },
        new[] { "IRONCLAD" }
    ])!;
Require(
    transientCombatGroups.SetEquals(["seapunk", "corpse_slug"]),
    "战斗结束时只应释放当前房间怪物资源，整局角色资源必须继续缓存供下一场复用。");

var runtimeScopeLeases = new RuntimeProviderScopeLeaseTracker();
var combatScopeLease = runtimeScopeLeases.Claim();
var merchantScopeLease = runtimeScopeLeases.Claim();
Require(
    !runtimeScopeLeases.IsCurrent(combatScopeLease),
    "商店已取得新的皮肤运行范围后，旧战斗房间退出时不能再把范围退回仅对局角色；" +
    "否则商人提供者会在 MerchantButton._Ready 前被停用。");
Require(
    runtimeScopeLeases.IsCurrent(merchantScopeLease),
    "当前商店房间必须持有可释放的最新运行范围租约。");
runtimeScopeLeases.Reset();
Require(
    !runtimeScopeLeases.IsCurrent(merchantScopeLease),
    "服务重置后，旧场景留下的运行范围租约必须全部失效。");

Require(
    RuntimePackWarmPolicy.ShouldWarm(32L * 1024L * 1024L, alreadyWarmed: false),
    "当前皮肤的 32 MiB 资源包应允许后台预读。");
Require(
    RuntimePackWarmPolicy.ShouldWarm(64L * 1024L * 1024L, alreadyWarmed: false),
    "64 MiB 边界资源包应允许后台预读。");
Require(
    !RuntimePackWarmPolicy.ShouldWarm(64L * 1024L * 1024L + 1L, alreadyWarmed: false),
    "大型 CZN 等资源包不能整包预读并挤占内存与磁盘带宽。");
Require(
    !RuntimePackWarmPolicy.ShouldWarm(32L * 1024L * 1024L, alreadyWarmed: true),
    "同一资源包在一次游戏会话内只能预读一次。");

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

Require(
    !RuntimeDependencyIsolationPolicy.CanReuseMountedProviderDependency(
        belongsToSelectedProvider: true,
        isProviderExclusivePath: false,
        isMountedBySelectedOverlay: true),
    "使用游戏公共路径的骨骼、图集和贴图必须进入本次皮肤的独立资源包，不能复用先加载皮肤留下的缓存。");
Require(
    RuntimeDependencyIsolationPolicy.CanReuseMountedProviderDependency(
        belongsToSelectedProvider: true,
        isProviderExclusivePath: true,
        isMountedBySelectedOverlay: true),
    "提供者独占路径中的大型依赖可以继续复用已挂载资源，避免大型皮肤重复打包。");
Require(
    !RuntimeDependencyIsolationPolicy.CanReuseMountedProviderDependency(
        belongsToSelectedProvider: true,
        isProviderExclusivePath: true,
        isMountedBySelectedOverlay: true,
        requiresAliasedLocation: true),
    "已进入独立别名空间的 Spine 图集必须同时复制其相对纹理页，不能复用原路径下的提供者资源。");

var compatibleFramework = new OptionalSkinFrameworkEvidence(
    DependentModId: "example.skin",
    DependencyId: "example.skin.framework",
    ReferencedAssemblyName: "example.skin.framework",
    HasDeclarativeSkinContract: true,
    ResourceClosureComplete: true);
Require(
    OptionalSkinFrameworkPolicy.CanSatisfyMissingDependency(
        compatibleFramework,
        ["example.skin.framework"]),
    "只有兼容层已提供同名程序集、且皮肤契约和资源闭包都完整时，框架依赖才可降为可选。");
Require(
    !OptionalSkinFrameworkPolicy.CanSatisfyMissingDependency(
        compatibleFramework with { ResourceClosureComplete = false },
        ["example.skin.framework"]),
    "皮肤包缺少契约引用资源时不能绕过原框架依赖。");
Require(
    !OptionalSkinFrameworkPolicy.CanSatisfyMissingDependency(
        compatibleFramework with { ReferencedAssemblyName = "unrelated.framework" },
        ["example.skin.framework"]),
    "不能仅凭依赖名称相似就把无关 DLL 依赖当成皮肤框架。");
Require(
    !OptionalSkinFrameworkPolicy.IsFrameworkHostRequired(
        "example.skin.framework",
        [compatibleFramework],
        ["example.skin.framework"]),
    "所有依赖者都已通过完整契约接管时，原框架宿主不应继续执行。");
Require(
    OptionalSkinFrameworkPolicy.IsFrameworkHostRequired(
        "example.skin.framework",
        [compatibleFramework, compatibleFramework with
        {
            DependentModId = "unsafe.skin",
            HasDeclarativeSkinContract = false
        }],
        ["example.skin.framework"]),
    "只要仍有一个依赖者不能安全接管，就必须保留原框架宿主。");
Require(
    OptionalSkinFrameworkPolicy.CanInstallCompatibilityAssembly(
        "example.skin.framework",
        [compatibleFramework]),
    "兼容层加载前必须允许全部依赖者都可安全接管的框架程序集。");
Require(
    !OptionalSkinFrameworkPolicy.CanInstallCompatibilityAssembly(
        "example.skin.framework",
        []),
    "没有任何皮肤需要兼容框架时不应抢先加载同名程序集。");
Require(
    OptionalSkinFrameworkPolicy.CanInstallCompatibilityAssembly(
        "example.skin.framework",
        [compatibleFramework, compatibleFramework with
        {
            DependentModId = "unsafe.skin",
            ResourceClosureComplete = false
        }]),
    "一个无法安全接管的依赖者不能阻止同框架下已验证完整的皮肤加载兼容程序集；" +
    "后续依赖绕过仍必须逐 Mod 判断。");
Require(
    !OptionalSkinFrameworkPolicy.CanInstallCompatibilityAssembly(
        "example.skin.framework",
        [compatibleFramework, compatibleFramework with
        {
            DependentModId = "unsafe.skin",
            ResourceClosureComplete = false
        }],
        originalFrameworkHostAvailable: true),
    "原框架已启用且仍有无法安全接管的依赖者时，不能抢占原框架程序集身份。");
Require(
    !OptionalSkinFrameworkPolicy.ShouldTreatAsGameplayBaseline(
        manifestAffectsGameplay: false,
        requiredByAnotherMod: true,
        exposesSelectableCosmetics: true),
    "被兼容补丁依赖的纯卡图包仍必须进入皮肤目录；依赖关系只要求保留其 DLL，" +
    "不能把已明确导出的卡图误当成玩法基线。");
Require(
    OptionalSkinFrameworkPolicy.ShouldTreatAsGameplayBaseline(
        manifestAffectsGameplay: false,
        requiredByAnotherMod: true,
        exposesSelectableCosmetics: false),
    "没有任何可选择外观的被依赖库仍应保守地作为玩法基线，避免隔离未知前置资源。");
Require(
    OptionalSkinFrameworkPolicy.ShouldTreatAsGameplayBaseline(
        manifestAffectsGameplay: true,
        requiredByAnotherMod: false,
        exposesSelectableCosmetics: true),
    "manifest 明确声明影响玩法时，不能因碰巧包含图片而降级成皮肤包。");

var editorStaticOwnership = ExternalCardProviderIdentityPolicy.ResolveEditorOwnership(
    hasOverride: true,
    fullArt: false,
    ancientTextOutside: false);
Require(
    editorStaticOwnership == new ExternalCardVisualOwnershipState(true, false, false),
    "外部编辑器只替换普通卡图时，应只拥有卡图层，不能顺带抢走卡框和文字层。");
var editorFullArtOwnership = ExternalCardProviderIdentityPolicy.ResolveEditorOwnership(
    hasOverride: true,
    fullArt: true,
    ancientTextOutside: false);
Require(
    editorFullArtOwnership == new ExternalCardVisualOwnershipState(true, true, true),
    "外部编辑器启用全卡图时，必须完整保留它的卡图、卡框和文字布局。");
var editorAncientTextOwnership = ExternalCardProviderIdentityPolicy.ResolveEditorOwnership(
    hasOverride: true,
    fullArt: false,
    ancientTextOutside: true);
Require(
    editorAncientTextOwnership == new ExternalCardVisualOwnershipState(true, false, true),
    "外部编辑器只把先古文字移到卡图外时，不应阻止 Skin Changer 恢复卡框。");
Require(
    ExternalCardProviderIdentityPolicy.ResolveEditorOwnership(
        hasOverride: false,
        fullArt: true,
        ancientTextOutside: true) == default,
    "没有实际卡图覆盖时，编辑器的残留显示设置不能伪造视觉所有权。");

Require(
    ExternalCardProviderIdentityPolicy.NeedsSyntheticPath(
        managerAvailable: true,
        isManagedTexture: true,
        resourcePath: string.Empty),
    "动态卡图管理器存在时，Skin Changer 生成的无路径贴图需要稳定资源身份，避免旧缓存写回。");
Require(
    !ExternalCardProviderIdentityPolicy.NeedsSyntheticPath(
        managerAvailable: false,
        isManagedTexture: true,
        resourcePath: string.Empty) &&
    !ExternalCardProviderIdentityPolicy.NeedsSyntheticPath(
        managerAvailable: true,
        isManagedTexture: false,
        resourcePath: string.Empty) &&
    !ExternalCardProviderIdentityPolicy.NeedsSyntheticPath(
        managerAvailable: true,
        isManagedTexture: true,
        resourcePath: "res://already_named.png"),
    "没有管理器、不是本 Mod 贴图或已有真实路径时，不能篡改资源身份。");
var providerPath = ExternalCardProviderIdentityPolicy.BuildSyntheticPath(
    "ZAP",
    "defect\nprovider-a\nres://zap.png");
Require(
    providerPath.StartsWith("user://skin_changer/card_provider/zap_", StringComparison.Ordinal) &&
    providerPath.EndsWith(".png", StringComparison.Ordinal),
    "合成资源路径必须保留卡牌身份，供能力型卡图管理器正确归属缓存。");
Require(
    providerPath == ExternalCardProviderIdentityPolicy.BuildSyntheticPath(
        "ZAP",
        "defect\nprovider-a\nres://zap.png") &&
    providerPath != ExternalCardProviderIdentityPolicy.BuildSyntheticPath(
        "ZAP",
        "defect\nprovider-b\nres://zap.png"),
    "相同来源的资源身份必须稳定，不同皮肤来源必须分离。");

Require(
    FrameworkEntryAnimationPolicy.Resolve(
        hasSelectedFrameworkSkin: true,
        hasEntryAnimation: true,
        currentAnimationId: "idle_loop",
        currentAnimationLoops: true) is
    {
        EntryAnimationId: "entry",
        QueuedAnimationId: "idle_loop",
        QueuedAnimationLoops: true
    },
    "选中的框架皮肤若提供登场动画，战斗模型必须先播放 entry 再回到原待机动画。");
Require(
    FrameworkEntryAnimationPolicy.Resolve(
        hasSelectedFrameworkSkin: false,
        hasEntryAnimation: true,
        currentAnimationId: "idle_loop",
        currentAnimationLoops: true) == null,
    "没有选中框架皮肤时不得把第三方 entry 动画注入原版角色。");

var civilightRoot =
    "/mnt/d/Programs/Steam/steamapps/workshop/content/2868840/3749568885";
if (Directory.Exists(civilightRoot))
{
    var contracts = FrameworkSkinContractScanner.Scan(civilightRoot, "CEdefect");
    Require(contracts.Count == 2, "同一框架皮肤包声明的两套角色皮肤必须拆成两个选项。");
    Require(
        contracts.Select(contract => contract.DisplayName).SequenceEqual(
            ["Civilight Eterna", "Condolences"]),
        "框架皮肤选项必须使用注册时的玩家可见名称，而不是 DLL 类型名。");
    Require(
        contracts.All(contract =>
            contract.TargetGroupId == "defect" &&
            contract.FrameworkAssemblyName == "thunninoiSkinManager"),
        "框架契约必须从泛型基类解析目标角色与所需兼容程序集。");
    Require(
        contracts[0].CharacterResources["CombatVisual"].EndsWith(
            "/default/ce_combat.tscn",
            StringComparison.OrdinalIgnoreCase) &&
        contracts[1].CharacterResources["CombatVisual"].EndsWith(
            "/epoque/ce_epoque_combat.tscn",
            StringComparison.OrdinalIgnoreCase),
        "两套皮肤的私有战斗场景不能被合并成同一个 Mod 级选项。");
    Require(
        contracts.All(contract => contract.CharacterResourceLists
            .GetValueOrDefault("EnergyLayers")?.Count == 5),
        "框架契约必须保留能量球的全部分层资源，而不是只取最后一张图。");
    Require(
        contracts.All(contract => contract.CharacterValues.Keys.ToHashSet().SetEquals(
        [
            "EnergyLabelColor",
            "EnergyLabelOutlineColor"
        ])),
        "Civilight Eterna 的角色能量数字和卡牌能量描边颜色必须同时进入接管契约。");
    Require(
        contracts.All(contract => contract.CharacterResources.Keys.ToHashSet().SetEquals(
        [
            "CombatVisual",
            "MerchantVisual",
            "RestVisual",
            "CharacterSelectBg",
            "CharacterSelectPortrait",
            "CharacterIcon",
            "CharacterIconOutline",
            "CharacterIconScene",
            "CharacterMapMarker",
            "CardFrameMaterial",
            "CardTrail",
            "EnergyIcon",
            "HandPoint",
            "HandRock",
            "HandPaper",
            "HandScissors"
        ])),
        "Civilight Eterna 的战斗、商店、休息、选角、头像、地图、卡框、能量与多人手势入口必须全部保留。");
    Require(
        contracts.All(contract => contract.Orbs
            .Select(orb => orb.TargetModelName)
            .ToHashSet()
            .SetEquals(["PlasmaOrb", "LightningOrb", "FrostOrb", "GlassOrb", "DarkOrb"]) &&
            contract.Orbs.All(orb =>
                orb.Resources.ContainsKey("CustomIconPath") &&
                orb.Resources.ContainsKey("CustomSpritePath") &&
                orb.Values.ContainsKey("CustomDarkenedColor"))),
        "框架契约必须保留五种充能球的图标、动态模型和暗色状态。");
    Require(
        contracts.All(contract => contract.Relics
            .Select(relic => relic.TargetModelName)
            .ToHashSet()
            .SetEquals(["CrackedCore", "InfusedCore"]) &&
            contract.Relics.All(relic =>
                relic.Resources.ContainsKey("PackedIconPath") &&
                relic.Resources.ContainsKey("PackedIconOutlinePath") &&
                relic.Resources.ContainsKey("BigIconPath"))),
        "框架契约必须保留破损核心与注能核心的小图、轮廓和大图。");

    var crackedCoreSmall = FrameworkRelicVisualPolicy.Resolve(
        contracts[0].Relics,
        "CrackedCore",
        largeIcon: false);
    Require(
        crackedCoreSmall != null &&
        crackedCoreSmall.IconPath.EndsWith(
            "/relics/theresa_dolls.png",
            StringComparison.OrdinalIgnoreCase) &&
        crackedCoreSmall.OutlinePath?.EndsWith(
            "/relics/theresa_outline.png",
            StringComparison.OrdinalIgnoreCase) == true,
        "框架遗物的小图刷新必须同时选择当前皮肤的小图与轮廓，不能继续使用首次缓存的原版素材。");

    var infusedCoreLarge = FrameworkRelicVisualPolicy.Resolve(
        contracts[0].Relics,
        "InfusedCore",
        largeIcon: true);
    Require(
        infusedCoreLarge is
        {
            OutlinePath: null
        } &&
        infusedCoreLarge.IconPath.EndsWith(
            "/relics/theresa_amiya_dolls.png",
            StringComparison.OrdinalIgnoreCase),
        "框架遗物的大图必须直接解析当前皮肤资源，不能经过游戏只解析一次的原版路径缓存。");

    Require(
        FrameworkRelicVisualPolicy.Resolve(
            contracts[0].Relics,
            "UnrelatedRelic",
            largeIcon: true) == null,
        "框架遗物刷新不能把一件遗物的素材泄漏到未声明的其它遗物。");
}

var frameworkManagerRoot =
    "/mnt/d/Programs/Steam/steamapps/workshop/content/2868840/3749563676";
if (Directory.Exists(frameworkManagerRoot))
{
    Require(
        FrameworkSkinContractScanner.Scan(
            frameworkManagerRoot,
            "thunninoiSkinManager").Count == 0,
        "框架宿主本身不能因为声明了通用基类就被误识别成一套可选择皮肤。");
}

var frameworkCompatibilityPath = Path.Combine(
    AppContext.BaseDirectory,
    "thunninoiSkinManager.dll");
Require(
    File.Exists(frameworkCompatibilityPath),
    "构建必须携带可独立满足框架程序集引用的轻量兼容层。");
Require(
    AssemblyName.GetAssemblyName(frameworkCompatibilityPath).Name == "thunninoiSkinManager",
    "兼容层的 CLR 程序集标识必须与皮肤 DLL 声明的框架引用完全一致。");

var earlyFrameworkSyncCalls = 0;
var earlyFrameworkSyncCount = FrameworkSelectionSynchronizer.Synchronize(
    Array.Empty<string>(),
    character =>
    {
        earlyFrameworkSyncCalls++;
        return character;
    },
    _ =>
    {
        earlyFrameworkSyncCalls++;
        return "selected";
    },
    (_, _) => earlyFrameworkSyncCalls++);
Require(
    earlyFrameworkSyncCount == 0 && earlyFrameworkSyncCalls == 0,
    "游戏模型数据库尚未注册任何角色时，框架选择同步必须完全跳过，不能读取固定角色并中断 SkinService 初始化。");

var synchronizedFrameworkSkins = new List<string>();
var readyFrameworkSyncCount = FrameworkSelectionSynchronizer.Synchronize(
    new[] { "DEFECT", "WATCHER" },
    character => character.ToLowerInvariant(),
    groupId => groupId == "defect" ? "ceterna" : null,
    (character, skinId) => synchronizedFrameworkSkins.Add(character + ":" + skinId));
Require(
    readyFrameworkSyncCount == 2 &&
    synchronizedFrameworkSkins.SequenceEqual(["DEFECT:ceterna", "WATCHER:default"]),
    "模型数据库就绪后，框架同步必须覆盖所有已注册角色，并让没有框架皮肤的角色回到默认选择。");

var deferredFrameworkRegistrations = new DeferredRegistrationQueue<string>();
var frameworkRegistrationCalls = 0;
Require(
    deferredFrameworkRegistrations.TryRegister(
        "CEdefect",
        "Civilight Eterna",
        isReady: false,
        _ => frameworkRegistrationCalls++) == DeferredRegistrationResult.Deferred &&
    frameworkRegistrationCalls == 0 &&
    deferredFrameworkRegistrations.PendingCount == 1,
    "游戏模型库未就绪时，框架皮肤注册必须排队，不能提前访问角色模型。");

try
{
    deferredFrameworkRegistrations.TryRegister(
        "CEdefect",
        "Civilight Eterna",
        isReady: true,
        _ =>
        {
            frameworkRegistrationCalls++;
            throw new KeyNotFoundException("CHARACTER.DEFECT");
        });
    throw new InvalidOperationException("框架注册失败必须向调用方报告异常。");
}
catch (KeyNotFoundException)
{
}
Require(
    frameworkRegistrationCalls == 1 &&
    deferredFrameworkRegistrations.PendingCount == 1 &&
    !deferredFrameworkRegistrations.IsCompleted("CEdefect"),
    "框架注册抛错后不能被误记为成功，必须保留后续重试资格。");

var completedFrameworkRegistrations = deferredFrameworkRegistrations.RetryPending(
    isReady: true,
    _ => frameworkRegistrationCalls++,
    (_, exception) => throw new InvalidOperationException(
        "模型库就绪后的框架注册重试不应失败。",
        exception));
Require(
    completedFrameworkRegistrations == 1 &&
    frameworkRegistrationCalls == 2 &&
    deferredFrameworkRegistrations.PendingCount == 0 &&
    deferredFrameworkRegistrations.IsCompleted("CEdefect"),
    "模型库就绪后必须自动补做先前失败的框架注册，并且只在成功后标记完成。");

Require(
    deferredFrameworkRegistrations.TryRegister(
        "CEdefect",
        "Civilight Eterna",
        isReady: true,
        _ => throw new InvalidOperationException("已完成的框架注册不应重复执行。")) ==
    DeferredRegistrationResult.AlreadyCompleted,
    "已完成的框架注册必须保持幂等，不能重复运行提供者注册回调。");

var isolatedHostContext = new AssemblyLoadContext(
    "skin-changer-framework-host-test",
    isCollectible: true);
var isolatedHostAssembly = isolatedHostContext.LoadFromAssemblyPath(
    typeof(OptionalSkinFrameworkPolicy).Assembly.Location);
var isolatedAdapterAssembly = FrameworkAssemblyLoadContextPolicy.LoadFromAssemblyPath(
    isolatedHostAssembly,
    frameworkCompatibilityPath);
Require(
    ReferenceEquals(
        AssemblyLoadContext.GetLoadContext(isolatedAdapterAssembly),
        isolatedHostContext),
    "内置兼容程序集必须加载到游戏宿主程序集所在的上下文，不能固定加载到 Default 上下文。" +
    "否则 Godot 自定义上下文中的 ModelId 会成为不同 CLR 类型，注册接口无法匹配。");
isolatedHostContext.Unload();

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

var fontLoadCount = 0;
var firstFont = new CachedResourceFixture(IsValid: true, Name: "first");
var secondFont = new CachedResourceFixture(IsValid: true, Name: "second");
var fontCache = new ReloadingReferenceCache<CachedResourceFixture>();
CachedResourceFixture? LoadFont() => ++fontLoadCount == 1 ? firstFont : secondFont;

Require(
    ReferenceEquals(fontCache.Get(LoadFont, font => font.IsValid), firstFont),
    "字体缓存第一次访问时必须加载游戏字体。");
Require(
    ReferenceEquals(fontCache.Get(LoadFont, font => font.IsValid), firstFont) &&
    fontLoadCount == 1,
    "仍然有效的游戏字体必须复用，不能在每次绘制控件时重复加载。");
firstFont.IsValid = false;
Require(
    ReferenceEquals(fontCache.Get(LoadFont, font => font.IsValid), secondFont) &&
    fontLoadCount == 2,
    "返回选角界面后若旧 FontVariation 已释放，必须丢弃静态缓存并重新加载；否则再次套用主题会白屏。");

Console.WriteLine("Skin Changer logic policy tests passed.");

internal static class DirectCharacterRuntimeFixture
{
    public const string TargetCharacterId = "REGENT";
    public const string DefaultCharacterId = "DEFECT";
}

internal static class ForeignCardProvider
{
    internal static class ConfigHelper
    {
        internal static bool IsAncientStyleEnabled(string cardTypeName) =>
            cardTypeName.Equals("SovereignBlade", StringComparison.Ordinal);
    }
}

internal sealed class ScopedMonsterProfileFixture(string monsterId)
{
    public ScopedMonsterTargetFixture Target { get; } = new(monsterId);
}

internal sealed class ScopedMonsterTargetFixture(string monsterId)
{
    public string MonsterId { get; } = monsterId;
}

internal sealed class NonPublicScopedMonsterProfileFixture(string monsterId)
{
    internal NonPublicScopedMonsterTargetFixture Target { get; } = new(monsterId);
}

internal sealed class NonPublicScopedMonsterTargetFixture(string monsterId)
{
    internal string MonsterId { get; } = monsterId;
}

internal sealed class CachedResourceFixture(bool IsValid, string Name)
{
    internal bool IsValid { get; set; } = IsValid;
    internal string Name { get; } = Name;
}
