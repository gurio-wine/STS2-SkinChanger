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
        hasManagedCombatScene: true),
    "局内热切换到任何包含战斗场景的角色皮肤时，都必须先通过兼容场景工厂实例化；" +
    "不能只处理带框架契约的皮肤，否则普通资源型与完整 DLL 皮肤会把 Node2D 强转失败。");
Require(
    !CharacterCombatSceneInstantiationPolicy.ShouldUseManagedFactory(
        isBaseSelection: true,
        hasManagedCombatScene: true),
    "游戏原皮必须继续使用游戏原生 CreateVisuals 路径。");
Require(
    !CharacterCombatSceneInstantiationPolicy.ShouldUseManagedFactory(
        isBaseSelection: false,
        hasManagedCombatScene: false),
    "只修改名称、头像或其它素材而没有战斗场景的皮肤不能拦截角色模型创建。");

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
        new RuntimeProviderScope([], IncludeRunWideMonsterProviders: false)).Count == 0,
    "启动阶段没有可见外观分组时，不得提前执行任何第三方皮肤初始化器。");
Require(
    RuntimeProviderScopePolicy.SelectActiveProviders(
            runtimeProviderCandidates,
            new RuntimeProviderScope(["WATCHER"], IncludeRunWideMonsterProviders: false))
        .SetEquals(["MeirinWatcherSkin"]),
    "选角界面只能激活当前预览角色，不能同时保留商人、先古或怪物皮肤代码。");
Require(
    RuntimeProviderScopePolicy.SelectActiveProviders(
            runtimeProviderCandidates,
            new RuntimeProviderScope(["pael"], IncludeRunWideMonsterProviders: false))
        .SetEquals(["AncientWaifus_Beta"]),
    "其它图鉴只应激活当前预览实体所属的交互提供者。");
Require(
    RuntimeProviderScopePolicy.SelectActiveProviders(
            runtimeProviderCandidates,
            new RuntimeProviderScope(["watcher"], IncludeRunWideMonsterProviders: true))
        .SetEquals(["MeirinWatcherSkin", "CznEnemySkin"]),
    "进入对局后应保留当前角色，并允许负责地图、背景和音乐的整局怪物提供者运行。");

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

Console.WriteLine("Skin Changer logic policy tests passed.");

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
