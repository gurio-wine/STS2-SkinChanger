using HarmonyLib;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Bestiary;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Rooms;
using STS2SkinChanger;
using System.Reflection;

var managedModListPatchType = typeof(Entry).Assembly.GetType(
                                  "STS2SkinChanger.Ui.ManagedModListNamePatch") ??
                              throw new InvalidOperationException("找不到 Mod 列表 [SC] 标记补丁类型。");
var managedModListPostfix = managedModListPatchType.GetMethod(
                                "Postfix",
                                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) ??
                            throw new InvalidOperationException("找不到 Mod 列表 [SC] 标记后置补丁。");
var managedModListPriority = managedModListPostfix.GetCustomAttributesData()
    .FirstOrDefault(attribute => attribute.AttributeType == typeof(HarmonyPriority))?
    .ConstructorArguments.FirstOrDefault().Value;
if (managedModListPriority is not int priority || priority != Priority.First)
{
    throw new InvalidOperationException(
        "[SC] 标记必须作为最先执行的后置补丁写入 Mod 行；否则正式版中不兼容的第三方列表补丁抛错后，所有标记都会消失。");
}

var patchType = typeof(Entry).Assembly.GetType(
                    "STS2SkinChanger.Core.FrameworkRelicPackedIconPatch") ??
                throw new InvalidOperationException("找不到框架遗物路径补丁类型。");
var relicGetters = patchType.GetMethod(
                        "RelicGetters",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) ??
                    throw new InvalidOperationException("找不到框架遗物 getter 目标枚举。");
var targets = ((IEnumerable<MethodBase>?)relicGetters.Invoke(
                   null,
                   ["PackedIconPath"]))?
              .ToArray() ?? [];
var baseGetter = AccessTools.PropertyGetter(typeof(RelicModel), "PackedIconPath") ??
                 throw new InvalidOperationException("游戏缺少 RelicModel.PackedIconPath getter。");

if (!targets.Contains(baseGetter))
{
    throw new InvalidOperationException(
        "框架遗物路径补丁漏掉了 RelicModel 基类 getter；继承原版路径的遗物会被通用后置补丁恢复成原图。");
}

var frameworkCombatPatchType = typeof(Entry).Assembly.GetType(
                                   "STS2SkinChanger.Core.FrameworkCombatVisualPatch") ??
                               throw new InvalidOperationException(
                                   "找不到框架角色战斗场景工厂补丁；Node2D 皮肤场景会被游戏原方法直接转换成 NCreatureVisuals 并中断战斗。");
var frameworkCombatPrefix = frameworkCombatPatchType.GetMethod(
                                "Prefix",
                                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) ??
                            throw new InvalidOperationException("找不到框架角色战斗场景工厂前置补丁。");
var frameworkCombatParameters = frameworkCombatPrefix.GetParameters();
if (frameworkCombatPrefix.ReturnType != typeof(bool) ||
    frameworkCombatParameters.Length != 2 ||
    frameworkCombatParameters[0].ParameterType != typeof(CharacterModel) ||
    frameworkCombatParameters[1].ParameterType != typeof(NCreatureVisuals).MakeByRefType())
{
    throw new InvalidOperationException(
        "框架角色战斗场景必须在 CharacterModel.CreateVisuals 原方法之前经场景工厂转换，不能依赖执行不到的后置替换。");
}
var frameworkCombatPriority = frameworkCombatPrefix.GetCustomAttributesData()
    .FirstOrDefault(attribute => attribute.AttributeType == typeof(HarmonyPriority))?
    .ConstructorArguments.FirstOrDefault().Value;
if (frameworkCombatPriority is not int combatPriority || combatPriority != Priority.First)
{
    throw new InvalidOperationException(
        "框架角色战斗场景工厂补丁必须最先执行，避免游戏先把允许为 Node2D 的框架场景强制转换成 NCreatureVisuals。");
}

var combatRoomCreate = typeof(NCombatRoom).GetMethod(
    nameof(NCombatRoom.Create),
    BindingFlags.Static | BindingFlags.Public,
    binder: null,
    types: [typeof(ICombatRoomVisuals), typeof(CombatRoomMode)],
    modifiers: null);
if (combatRoomCreate == null)
{
    throw new InvalidOperationException(
        "当前游戏版本缺少 NCombatRoom.Create(ICombatRoomVisuals, CombatRoomMode)，" +
        "无法在创建战斗场景前收窄怪物皮肤运行期。");
}

var skinServiceType = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.SkinService") ??
                      throw new InvalidOperationException("找不到 SkinService 类型。");
var focusRuntimeProviders = skinServiceType.GetMethod(
    "FocusRuntimeProviderBehaviorsOnGroups",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
    binder: null,
    types: [typeof(IEnumerable<string>), typeof(IReadOnlyCollection<string>), typeof(string)],
    modifiers: null);
if (focusRuntimeProviders == null)
{
    throw new InvalidOperationException(
        "缺少按可见分组与地区环境授权分别收窄运行期提供者的方法；" +
        "否则单只怪物皮肤仍会错误启用整局背景和音乐。");
}

var managedLoaderType = typeof(Entry).Assembly.GetType(
                            "STS2SkinChanger.Core.ManagedSkinModLoader") ??
                        throw new InvalidOperationException("找不到托管皮肤提供者加载器。");
var externalCardVisualBridge = typeof(Entry).Assembly.GetType(
                                   "STS2SkinChanger.Core.ExternalCardVisualBridge") ??
                               throw new InvalidOperationException("找不到外部卡牌视觉管理器兼容桥。");
var nodeOwnershipProbe = externalCardVisualBridge.GetMethod(
    "GetOwnership",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
    binder: null,
    types: [typeof(MegaCrit.Sts2.Core.Nodes.Cards.NCard)],
    modifiers: null);
var providerSynchronizer = externalCardVisualBridge.GetMethod(
    "SynchronizeProvider",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
    binder: null,
    types: [typeof(MegaCrit.Sts2.Core.Nodes.Cards.NCard)],
    modifiers: null);
if (nodeOwnershipProbe == null || providerSynchronizer?.ReturnType != typeof(void))
{
    throw new InvalidOperationException(
        "动态卡图编辑器兼容必须按实际 NCard 读取覆盖状态，并在最终呈现后同步提供者缓存；" +
        "仅按 CardModel 探测会漏掉脚本管理器维护的节点元数据。");
}
var configureRunEnvironment = managedLoaderType.GetMethod(
    "ConfigureRunEnvironmentProviders",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
    binder: null,
    types: [typeof(IEnumerable<string>)],
    modifiers: null);
if (configureRunEnvironment == null)
{
    throw new InvalidOperationException(
        "托管皮肤提供者缺少独立的地区环境行为开关，无法隔离背景、地图音乐和战斗 BGM。");
}

var refreshRunEnvironment = managedLoaderType.GetMethod(
    "RefreshRunEnvironmentPresentation",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
    binder: null,
    types: Type.EmptyTypes,
    modifiers: null);
if (refreshRunEnvironment == null)
{
    throw new InvalidOperationException(
        "地区环境切换后必须能按当前地图/战斗状态统一重建 BGM，而不是直接调用地图音乐更新。");
}

var inRunScopePatch = RequirePatchType(
    "STS2SkinChanger.Ui.InRunCharacterAppearanceRuntimePatch",
    "缺少进入对局时的皮肤运行期范围补丁。");
var inRunScopePrefix = RequirePatchMethod(inRunScopePatch, "Prefix");
if (inRunScopePrefix.ReturnType != typeof(void) ||
    inRunScopePrefix.GetParameters() is not [{ ParameterType: var runType }] ||
    runType.FullName != "MegaCrit.Sts2.Core.Nodes.NRun")
{
    throw new InvalidOperationException(
        "地区环境范围必须在 NRun._Ready 原方法播放地图音乐之前建立，不能留到后置阶段。");
}

var appearanceRuntimeType = typeof(Entry).Assembly.GetType(
                                "STS2SkinChanger.Ui.CharacterAppearanceRuntime") ??
                            throw new InvalidOperationException("找不到局内外观运行时。");
var combatBackgroundRefresh = appearanceRuntimeType.GetMethod(
    "RefreshCurrentCombatBackground",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
    binder: null,
    types: Type.EmptyTypes,
    modifiers: null);
if (combatBackgroundRefresh?.ReturnType != typeof(bool))
{
    throw new InvalidOperationException(
        "局内修改怪物皮肤或地区优先级后必须通过统一入口重建当前战斗背景。");
}

var contextualControlsType = typeof(Entry).Assembly.GetType(
                                 "STS2SkinChanger.Ui.ContextualSkinControls") ??
                             throw new InvalidOperationException("找不到图鉴皮肤控件类型。");
var bestiaryNameRefresh = contextualControlsType.GetMethod(
    "RefreshBestiaryMonsterNames",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
    binder: null,
    types: [typeof(NBestiary)],
    modifiers: null);
if (bestiaryNameRefresh == null)
{
    throw new InvalidOperationException(
        "地区皮肤优先级变化后必须刷新怪物图鉴当前地区已有条目与选中怪物的名称。");
}

var appearanceScreenType = typeof(Entry).Assembly.GetType(
                               "STS2SkinChanger.Ui.CharacterAppearanceScreen") ??
                           throw new InvalidOperationException("找不到局内外观界面。");
var bossMapTargetSelection = appearanceScreenType.GetMethod(
    "SelectBossMapTarget",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
    binder: null,
    types: [typeof(NBossMapPoint), typeof(Vector2)],
    modifiers: null);
if (bossMapTargetSelection?.ReturnType != typeof(bool))
{
    throw new InvalidOperationException(
        "地图外观模式必须能把 Boss 大图标作为只切换皮肤的目标。");
}
var bossPresentationRefresh = appearanceRuntimeType.GetMethod(
    "RefreshCurrentBossPresentation",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
if (bossPresentationRefresh == null ||
    bossPresentationRefresh.GetParameters() is not [{ ParameterType: var affectedGroupsType }] ||
    affectedGroupsType != typeof(IReadOnlySet<string>))
{
    throw new InvalidOperationException(
        "局内修改本阶段 Boss 皮肤后必须有统一刷新地图大图标、顶部图标和悬浮名称的入口。");
}

var nativeBossMapRefresh = appearanceRuntimeType.GetMethod(
    "RefreshNativeBossMapPoint",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
if (nativeBossMapRefresh == null ||
    nativeBossMapRefresh.GetParameters() is not
        [{ ParameterType: var nativeBossPointType }, { ParameterType: var runStateType }] ||
    nativeBossPointType != typeof(NBossMapPoint) ||
    runStateType.FullName != "MegaCrit.Sts2.Core.Runs.IRunState")
{
    throw new InvalidOperationException(
        "Boss 地图大图标必须能从当前游戏资源重建；仅重放 DLL 皮肤回调会漏掉纯资源皮肤。 ");
}

var bossMapPointPresentationPatch = RequirePatchType(
    "STS2SkinChanger.Ui.BossMapPointSkinPresentationPatch",
    "缺少 Boss 地图大图标的托管初始化补丁；新生成的地图节点不会应用当前皮肤。");
var bossMapPointPostfix = RequirePatchMethod(bossMapPointPresentationPatch, "Postfix");
if (bossMapPointPostfix.GetParameters() is not [{ ParameterType: var bossMapPointType }] ||
    bossMapPointType != typeof(NBossMapPoint))
{
    throw new InvalidOperationException(
        "Boss 地图大图标初始化必须把当前 NBossMapPoint 交给统一的可逆皮肤流程。");
}

var managedBossReadyTarget = managedLoaderType.GetMethod(
    "IsNodeReadyPresentationTarget",
    BindingFlags.Static | BindingFlags.NonPublic) ??
    throw new InvalidOperationException("找不到第三方场景初始化隔离判定。");
var bossReady = AccessTools.Method(typeof(NBossMapPoint), nameof(NBossMapPoint._Ready)) ??
                throw new InvalidOperationException("当前游戏版本缺少 NBossMapPoint._Ready。");
if (managedBossReadyTarget.Invoke(null, [bossReady]) is not true)
{
    throw new InvalidOperationException(
        "第三方 Boss 地图 _Ready 补丁必须由 Skin Changer 隔离并可逆重放；" +
        "否则切离 CZN 后会残留其地图大图标。");
}

var multiplayerMerchantPatch = RequirePatchType(
    "STS2SkinChanger.Core.MultiplayerMerchantPlayerVisualIsolationPatch",
    "缺少商店多人角色外观隔离补丁；同角色玩家会全部显示本机选择的皮肤。");
var merchantTarget = (MethodBase?)RequirePatchMethod(multiplayerMerchantPatch, "TargetMethod")
    .Invoke(null, null);
if (merchantTarget?.DeclaringType != typeof(NMerchantRoom) ||
    merchantTarget.Name != "AfterRoomIsLoaded")
{
    throw new InvalidOperationException(
        "商店多人隔离必须接管 NMerchantRoom.AfterRoomIsLoaded 的逐玩家创建过程。");
}
var merchantIsolationPrefix = RequirePatchMethod(multiplayerMerchantPatch, "Prefix");
var merchantIsolationParameters = merchantIsolationPrefix.GetParameters();
if (merchantIsolationPrefix.ReturnType != typeof(bool) ||
    merchantIsolationParameters.Length != 3 ||
    merchantIsolationParameters.Any(parameter =>
        parameter.Name == null ||
        !parameter.Name.StartsWith("___", StringComparison.Ordinal) ||
        AccessTools.Field(typeof(NMerchantRoom), parameter.Name[3..])?.FieldType !=
        parameter.ParameterType))
{
    throw new InvalidOperationException(
        "商店多人隔离必须完整替代原创建循环，并保留玩家、容器和可视列表三个原字段。");
}

var restCreatePatch = RequirePatchType(
    "STS2SkinChanger.Core.MultiplayerRestSiteCreateScopePatch",
    "缺少休息点角色创建隔离补丁；远端玩家会复用本机角色皮肤。");
RequirePlayerScopePatch(restCreatePatch, typeof(Player));

var restReadyPatch = RequirePatchType(
    "STS2SkinChanger.Core.MultiplayerRestSiteReadyScopePatch",
    "缺少休息点角色初始化隔离补丁；延迟加载的骨骼和素材会窜用本机皮肤。");
RequirePlayerScopePatch(restReadyPatch, typeof(NRestSiteCharacter));

var handReadyPatch = RequirePatchType(
    "STS2SkinChanger.Core.MultiplayerTreasureHandReadyScopePatch",
    "缺少遗物宝箱手部初始贴图隔离补丁；远端玩家会显示本机皮肤的手。");
RequirePlayerScopePatch(handReadyPatch, typeof(NHandImage));

var handMovePatch = RequirePatchType(
    "STS2SkinChanger.Core.MultiplayerTreasureHandMoveScopePatch",
    "缺少遗物争夺动作贴图隔离补丁；石头剪刀布动作会重新窜回本机皮肤。");
var handMoveTarget = (MethodBase?)RequirePatchMethod(handMovePatch, "TargetMethod")
    .Invoke(null, null);
if (handMoveTarget?.DeclaringType != typeof(NHandImage) ||
    handMoveTarget.Name != "SetTextureToFightMove")
{
    throw new InvalidOperationException(
        "遗物争夺动作隔离必须覆盖 NHandImage.SetTextureToFightMove。");
}
RequirePlayerScopePatch(handMovePatch, typeof(NHandImage));

Console.WriteLine("Skin Changer runtime patch target tests passed.");

static Type RequirePatchType(string name, string error) =>
    typeof(Entry).Assembly.GetType(name) ?? throw new InvalidOperationException(error);

static MethodInfo RequirePatchMethod(Type patchType, string methodName) =>
    patchType.GetMethod(
        methodName,
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) ??
    throw new InvalidOperationException($"找不到 {patchType.Name}.{methodName}。");

static void RequirePlayerScopePatch(Type patchType, Type ownerType)
{
    var prefix = RequirePatchMethod(patchType, "Prefix");
    var prefixParameters = prefix.GetParameters();
    if (prefix.ReturnType != typeof(void) ||
        prefixParameters.Length != 2 ||
        prefixParameters[0].ParameterType != ownerType ||
        prefixParameters[1].ParameterType != typeof(IDisposable).MakeByRefType())
    {
        throw new InvalidOperationException(
            $"{patchType.Name} 必须在整个调用期间保存对应玩家的皮肤上下文。");
    }

    var finalizer = RequirePatchMethod(patchType, "Finalizer");
    var finalizerParameters = finalizer.GetParameters();
    if (finalizer.ReturnType != typeof(Exception) ||
        finalizerParameters.Length != 2 ||
        finalizerParameters[0].ParameterType != typeof(Exception) ||
        finalizerParameters[1].ParameterType != typeof(IDisposable))
    {
        throw new InvalidOperationException(
            $"{patchType.Name} 必须用 Harmony finalizer 在成功和异常路径都释放皮肤上下文。");
    }
}
