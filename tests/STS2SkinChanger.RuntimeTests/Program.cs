using HarmonyLib;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.sts2.Core.Nodes.TopBar;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Bestiary;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Rooms;
using STS2SkinChanger;
using System.Reflection;

if (args.Length == 2 && args[0] == "--audit-provider-settings")
{
    ProviderSettingsTests.Audit(args[1]);
    return;
}

if (args.Length == 2 && args[0] == "--audit-preserved-settings")
{
    ProviderSettingsTests.AuditPreserved(args[1]);
    return;
}

if (args.Length == 2 && args[0] == "--audit-slot-toggle")
{
    SlotVisibilityTests.Audit(args[1]);
    return;
}

if (args.Length == 2 && args[0] == "--audit-provider-animation")
{
    ProviderAnimationCompatibilityTests.Audit(args[1]);
    return;
}

if (args.Length == 3 && args[0] == "--audit-direct-character-runtime")
{
    DirectCharacterRuntimeTests.Audit(args[1], args[2]);
    return;
}

if (args.Length == 2 && args[0] == "--audit-base-lib-duplicates")
{
    DuplicateProviderLoadingTests.Run(args[1]);
    return;
}

if (args.Length >= 5 && args[0] == "--audit-duplicate-providers")
{
    DuplicateProviderLoadingTests.AuditInstalled(args[1], args[2], args.Skip(3).ToArray());
    return;
}

if (args.Length == 2 && args[0] == "--audit-provider-folder")
{
    ProviderLookupTests.AuditFolder(args[1]);
    return;
}

ProviderSettingsTests.Run();
SlotVisibilityTests.Run();
ProviderAnimationCompatibilityTests.Run();
AppearanceControlContractTests.Run();
ProviderLookupTests.Run();
DuplicateProviderLoadingTests.Run();
CreatureVisualLifecycleTests.Run();
CharacterSkinBundleContractTests.Run();
CharacterSkinPopupContractTests.Run();
ScrollListRebuildContractTests.Run();
CardSkinRefreshContractTests.Run();
CardProviderBridgeContractTests.Run();
SceneAppearanceLifecycleTests.Run();
LoadOrderSafetyTests.Run();
var declaredVersion = Version.Parse(Entry.InternalTestVersion);
var expectedAssemblyVersion = new Version(
    declaredVersion.Major, declaredVersion.Minor, declaredVersion.Build,
    Math.Max(0, declaredVersion.Revision));
if (typeof(Entry).Assembly.GetName().Version != expectedAssemblyVersion)
{
    throw new InvalidOperationException("启动日志的内测版本必须与实际程序集版本一致，避免误报旧部署版本。");
}

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
var skinConfigType = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.SkinConfig") ??
                     throw new InvalidOperationException("找不到 SkinConfig 类型。");
var multiplayerSyncType = typeof(Entry).Assembly.GetType(
                              "STS2SkinChanger.Core.MultiplayerSkinSync") ??
                          throw new InvalidOperationException("找不到联机皮肤同步类型。");
var sendChangesProperty = skinConfigType.GetProperty(
                              "MultiplayerSkinSyncEnabled",
                              BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ??
                          throw new InvalidOperationException("找不到发送皮肤改变配置。");
var receiveChangesProperty = skinConfigType.GetProperty(
                                 "LoadOtherPlayersCustomSkins",
                                 BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ??
                             throw new InvalidOperationException("找不到接收皮肤改变配置。");
var skinConfigBackingField = skinServiceType.GetField(
                                 "<Config>k__BackingField",
                                 BindingFlags.Static | BindingFlags.NonPublic) ??
                             throw new InvalidOperationException("找不到 SkinService.Config 存储字段。");
var configLoadedField = skinServiceType.GetField(
                            "_configLoaded",
                            BindingFlags.Static | BindingFlags.NonPublic) ??
                        throw new InvalidOperationException("找不到 SkinService 配置加载状态。");
var testConfig = Activator.CreateInstance(skinConfigType) ??
                 throw new InvalidOperationException("无法建立隔离的联机测试配置。");
skinConfigBackingField.SetValue(null, testConfig);
configLoadedField.SetValue(null, true);
var appendCapabilityTrailer = multiplayerSyncType.GetMethod(
                                  "AppendCapabilityTrailer",
                                  BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) ??
                              throw new InvalidOperationException("找不到联机能力握手写入方法。");
var readCapabilityTrailer = multiplayerSyncType.GetMethod(
                                "TryReadCapabilityTrailer",
                                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) ??
                            throw new InvalidOperationException("找不到联机能力握手读取方法。");
var getAvailableSelectionMaps = multiplayerSyncType.GetMethod(
                                    "GetAvailableSelectionMaps",
                                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) ??
                                throw new InvalidOperationException("找不到远端玩家皮肤映射读取方法。");
var sessionSelectionType = typeof(Entry).Assembly.GetType(
                               "STS2SkinChanger.Core.SessionCharacterSelection") ??
                           throw new InvalidOperationException("找不到联机玩家皮肤快照类型。");
var characterCombatTransformType = typeof(Entry).Assembly.GetType(
                                       "STS2SkinChanger.Core.CharacterCombatTransform") ??
                                   throw new InvalidOperationException("找不到角色局内外观参数类型。");
var availableSelectionsField = multiplayerSyncType.GetField(
                                   "AvailableSelections",
                                   BindingFlags.Static | BindingFlags.NonPublic) ??
                               throw new InvalidOperationException("找不到联机玩家皮肤快照缓存。");

sendChangesProperty.SetValue(testConfig, false);
receiveChangesProperty.SetValue(testConfig, false);
var disabledSyncWriter = new PacketWriter();
appendCapabilityTrailer.Invoke(null, [disabledSyncWriter]);
if (disabledSyncWriter.BitPosition != 0)
{
    throw new InvalidOperationException(
        "关闭联机皮肤同步后仍修改了游戏握手包；未安装 Skin Changer 的玩家仍会收到额外数据。");
}

sendChangesProperty.SetValue(testConfig, false);
receiveChangesProperty.SetValue(testConfig, true);
var enabledSyncWriter = new PacketWriter();
appendCapabilityTrailer.Invoke(null, [enabledSyncWriter]);
if (enabledSyncWriter.BitPosition != 72)
{
    throw new InvalidOperationException(
        "只接收皮肤改变时没有写入能力标记，其他玩家将无法确认本机能够接收。");
}

sendChangesProperty.SetValue(testConfig, false);
receiveChangesProperty.SetValue(testConfig, false);
object?[] disabledReadArguments = [enabledSyncWriter.Buffer, (byte)0];
if (readCapabilityTrailer.Invoke(null, disabledReadArguments) is not false)
{
    throw new InvalidOperationException(
        "关闭联机皮肤同步后仍解析了其他玩家的能力握手。");
}

sendChangesProperty.SetValue(testConfig, true);
receiveChangesProperty.SetValue(testConfig, false);
object?[] enabledReadArguments = [enabledSyncWriter.Buffer, (byte)0];
if (readCapabilityTrailer.Invoke(null, enabledReadArguments) is not true ||
    enabledReadArguments[1] is not byte protocolVersion ||
    protocolVersion != 9 ||
    System.Text.Encoding.ASCII.GetString(enabledSyncWriter.Buffer, 0, 8) != "GSCAP09!")
{
    throw new InvalidOperationException(
        "只发送皮肤改变时无法读取当前协议的能力握手。");
}

var netMessageType = typeof(Entry).Assembly.GetType(
                         "STS2SkinChanger.Core.SkinChangerNetMessage") ??
                     throw new InvalidOperationException("找不到 Skin Changer 联机消息。");
var sourceManifestField = netMessageType.GetField(
                              "SourceOptionManifest",
                              BindingFlags.Instance | BindingFlags.Public |
                              BindingFlags.NonPublic) ??
                          throw new InvalidOperationException(
                              "联机消息没有携带合并皮肤的有序来源。");
var netMessage = Activator.CreateInstance(netMessageType) ??
                 throw new InvalidOperationException("无法建立联机消息测试对象。");
netMessageType.GetField("ProtocolVersion")!.SetValue(netMessage, (byte)9);
netMessageType.GetField("Kind")!.SetValue(
    netMessage,
    Enum.ToObject(netMessageType.GetField("Kind")!.FieldType, 1));
netMessageType.GetField("PlayerNetId")!.SetValue(netMessage, 42UL);
netMessageType.GetField("CharacterId")!.SetValue(netMessage, "REGENT");
netMessageType.GetField("GroupId")!.SetValue(netMessage, "character:regent");
netMessageType.GetField("OptionId")!.SetValue(netMessage, "composition:test");
sourceManifestField.SetValue(netMessage, "[\"primary\",\"fallback\"]");
netMessageType.GetField("TransformManifest")!.SetValue(netMessage, "{}");
var messageWriter = new PacketWriter();
netMessageType.GetMethod("Serialize")!.Invoke(netMessage, [messageWriter]);
var messageReader = new PacketReader();
messageReader.Reset(messageWriter.Buffer);
var decodedMessage = Activator.CreateInstance(netMessageType) ??
                     throw new InvalidOperationException("无法建立联机消息读取对象。");
netMessageType.GetMethod("Deserialize")!.Invoke(decodedMessage, [messageReader]);
if (sourceManifestField.GetValue(decodedMessage) as string !=
    "[\"primary\",\"fallback\"]")
{
    throw new InvalidOperationException("合并皮肤来源没有按顺序完成联机消息往返。");
}

var tryParseSourceManifest = multiplayerSyncType.GetMethod(
                                 "TryParseSourceOptionManifest",
                                 BindingFlags.Static | BindingFlags.Public |
                                 BindingFlags.NonPublic) ??
                             throw new InvalidOperationException(
                                 "缺少联机合并皮肤来源校验入口。");
var serializeSourceManifest = multiplayerSyncType.GetMethod(
                                  "SerializeSourceOptionManifest",
                                  BindingFlags.Static | BindingFlags.Public |
                                  BindingFlags.NonPublic) ??
                              throw new InvalidOperationException(
                                  "缺少本机皮肤来源清单生成入口。");
if (serializeSourceManifest.Invoke(null, [Array.Empty<string>()]) as string != "[]" ||
    serializeSourceManifest.Invoke(null, [new[] { "single-skin" }]) as string !=
    "[\"single-skin\"]")
{
    throw new InvalidOperationException(
        "游戏原皮必须广播空来源，普通皮肤必须广播唯一来源。");
}
object?[] validSourceArguments = ["[\"installed-a\",\"missing-b\",\"installed-c\"]", null];
if (tryParseSourceManifest.Invoke(null, validSourceArguments) is not true ||
    validSourceArguments[1] is not IReadOnlyList<string> validSources ||
    !validSources.SequenceEqual(["installed-a", "missing-b", "installed-c"]))
{
    throw new InvalidOperationException("合法的合并皮肤来源顺序没有被保留。");
}

foreach (var invalidManifest in new[]
         {
             "[\"valid\",\"\"]",
             "[\"duplicate\",\"DUPLICATE\"]",
             System.Text.Json.JsonSerializer.Serialize(
                 Enumerable.Range(0, 65).Select(index => $"skin-{index}").ToArray()),
             new string('x', 32769)
         })
{
    object?[] invalidSourceArguments = [invalidManifest, null];
    if (tryParseSourceManifest.Invoke(null, invalidSourceArguments) is not false)
    {
        throw new InvalidOperationException(
            "联机合并皮肤来源校验接受了空项、重复项或超限数据。");
    }
}

var staleTransforms = Activator.CreateInstance(
                          typeof(Dictionary<,>).MakeGenericType(
                              typeof(string),
                              characterCombatTransformType),
                          StringComparer.OrdinalIgnoreCase) ??
                      throw new InvalidOperationException("无法建立联机玩家外观参数快照。");
var staleSelection = Activator.CreateInstance(
                         sessionSelectionType,
                         "REGENT",
                         "character:regent",
                         "provider:test",
                         new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                         {
                             ["character:regent"] = "provider:test"
                         },
                         staleTransforms,
                         true) ??
                     throw new InvalidOperationException("无法建立联机玩家皮肤快照。");
var availableSelections = availableSelectionsField.GetValue(null) as System.Collections.IDictionary ??
                          throw new InvalidOperationException("联机玩家皮肤快照缓存类型不兼容。");
availableSelections[99UL] = staleSelection;
receiveChangesProperty.SetValue(testConfig, false);
var disabledSelectionMaps = getAvailableSelectionMaps.Invoke(null, null)
                            as System.Collections.IEnumerable ??
                            throw new InvalidOperationException("无法读取关闭同步后的远端皮肤映射。");
if (disabledSelectionMaps.Cast<object>().Any())
{
    throw new InvalidOperationException(
        "关闭接收皮肤改变后仍向运行时暴露旧的远端玩家皮肤，可能继续覆盖模型或头像。");
}
availableSelections.Clear();
receiveChangesProperty.SetValue(testConfig, true);
var characterPreviewApply = skinServiceType.GetMethod(
    "ApplyCharacterPreviewSelection",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
    binder: null,
    types: [typeof(string), typeof(string)],
    modifiers: null);
var characterPreviewClear = skinServiceType.GetMethod(
    "ClearCharacterPreviewSelection",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
    binder: null,
    types: [typeof(bool)],
    modifiers: null);
if (characterPreviewApply?.ReturnType != typeof(bool) ||
    characterPreviewClear?.ReturnType != typeof(bool))
{
    throw new InvalidOperationException(
        "选角皮肤悬浮必须使用不写配置的临时选择层，并能在列表关闭后恢复持久选择。");
}
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

var bestiaryInitialNamePatch = typeof(Entry).Assembly.GetType(
    "STS2SkinChanger.Ui.BestiaryInitialSkinNamePatch") ??
    throw new InvalidOperationException("怪物图鉴缺少首次皮肤名称刷新补丁。");
var bestiaryHarmony = new Harmony("Gurio.SkinChanger.Tests.BestiaryInitialNames");
try
{
    bestiaryHarmony.CreateClassProcessor(bestiaryInitialNamePatch).Patch();
    var target = AccessTools.Method(typeof(NBestiary), nameof(NBestiary.OnSubmenuOpened)) ??
                 throw new InvalidOperationException("当前游戏版本缺少怪物图鉴打开入口。");
    if (Harmony.GetPatchInfo(target)?.Owners.Contains(bestiaryHarmony.Id) != true)
    {
        throw new InvalidOperationException("怪物图鉴首次名称刷新没有挂到条目生成完成之后。");
    }
}
finally
{
    bestiaryHarmony.UnpatchAll(bestiaryHarmony.Id);
}

foreach (var methodName in new[]
         {
             "GetMonsterSkinPresets", "CreateMonsterSkinPreset", "OverwriteMonsterSkinPreset",
             "RenameMonsterSkinPreset", "DeleteMonsterSkinPreset", "ApplyMonsterSkinPreset"
         })
{
    if (skinServiceType.GetMethod(
            methodName,
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) == null)
    {
        throw new InvalidOperationException($"怪物图鉴预设缺少持久化边界：{methodName}。");
    }
}

foreach (var methodName in new[]
         {
             "GetCharacterSkinBundles", "CreateCharacterSkinBundle",
             "OverwriteCharacterSkinBundle", "RenameCharacterSkinBundle",
             "DeleteCharacterSkinBundle", "GetCharacterSkinBundleCharacterOption",
             "ApplySelectedCharacterSkinBundleForRun", "RestoreCharacterSkinBundleAfterRun",
             "GetCardPresetCategories", "GetMonsterPresetCategories"
         })
{
    if (skinServiceType.GetMethod(
            methodName,
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) == null)
    {
        throw new InvalidOperationException($"皮肤包服务缺少边界：{methodName}。");
    }
}
var presetConfigPath = Path.Combine(Path.GetTempPath(), $"skin-changer-monster-preset-{Guid.NewGuid():N}.json");
var presetRoundTripPath = presetConfigPath + ".roundtrip";
try
{
    File.WriteAllText(presetConfigPath, """
        {"MonsterSkinPresets":[{"Name":"Act 1","CategoryId":"act:one",
        "Priority":[{"OptionId":"skin:czn","Enabled":true}],
        "Selections":{"monster:jaw_worm":"skin:czn"},
        "FollowingGroupIds":["monster:jaw_worm"]}],
        "ActiveMonsterSkinPresets":{"act:one":"Act 1"}}
        """);
    var loadedConfig = skinConfigType.GetMethod(
        "Load", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
        .Invoke(null, [presetConfigPath])!;
    skinConfigType.GetMethod(
        "Save", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
        .Invoke(loadedConfig, [presetRoundTripPath]);
    using var presetDocument = System.Text.Json.JsonDocument.Parse(File.ReadAllText(presetRoundTripPath));
    var root = presetDocument.RootElement;
    if (root.GetProperty("MonsterSkinPresets").GetArrayLength() != 1 ||
        root.GetProperty("ActiveMonsterSkinPresets").GetProperty("act:one").GetString() != "Act 1")
    {
        throw new InvalidOperationException("怪物皮肤预设和当前地区预设未能经过配置文件往返保存。");
    }
}
finally
{
    foreach (var path in new[]
             {
                 presetConfigPath, presetConfigPath + ".bak", presetRoundTripPath,
                 presetRoundTripPath + ".bak", presetRoundTripPath + ".tmp"
             })
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
var monsterPresetLocalizationType = typeof(Entry).Assembly.GetType(
    "STS2SkinChanger.Core.ModLocalization") ??
    throw new InvalidOperationException("找不到怪物预设本地化服务。");
var noMonsterPresetTexts = (IReadOnlyDictionary<string, string>?)monsterPresetLocalizationType.GetField(
    "NoMonsterPresetTexts", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) ??
    throw new InvalidOperationException("怪物预设缺少本地化文本。");
if (noMonsterPresetTexts.Count != 15 || noMonsterPresetTexts.Values.Any(string.IsNullOrWhiteSpace))
{
    throw new InvalidOperationException("怪物预设提示必须覆盖工坊使用的全部 15 种语言。");
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

var roomIconUpdate = AccessTools.Method(typeof(NTopBarRoomIcon), "UpdateIcon") ??
                     throw new InvalidOperationException("当前游戏缺少房间图标刷新入口。");
var roomIconPatch = RequirePatchType(
    "STS2SkinChanger.Ui.TopBarRoomIconSkinTexturePatch",
    "房间图标仍绕过通用皮肤贴图替换流程。");
var roomIconTranspiler = RequirePatchMethod(roomIconPatch, "Transpiler");
var compressedTextureLoader = AccessTools.Method(typeof(AssetCache), nameof(AssetCache.GetCompressedTexture2D));
var managedTextureLoader = AccessTools.Method(typeof(AssetCache), nameof(AssetCache.GetTexture2D));
var originalRoomIconInstructions = PatchProcessor.GetOriginalInstructions(roomIconUpdate);
if (originalRoomIconInstructions.Count(instruction => instruction.Calls(compressedTextureLoader)) != 2)
    throw new InvalidOperationException("房间图标原方法已变化，请重新核对图标与描边的读取方式。");
var roomIconInstructions = ((IEnumerable<CodeInstruction>)roomIconTranspiler.Invoke(
    null, new object[] { originalRoomIconInstructions })!).ToArray();
if (roomIconInstructions.Any(instruction => instruction.Calls(compressedTextureLoader)) ||
    roomIconInstructions.Count(instruction => instruction.Calls(managedTextureLoader)) != 2)
    throw new InvalidOperationException("房间图标及描边都必须使用支持托管皮肤的 Texture2D 加载路径。");
var roomIconTestHarmony = new Harmony("Gurio.SkinChanger.RuntimeTests.RoomIcon");
try
{
    // Compile the patched game method as well as inspecting it, catching invalid IL/type changes.
    roomIconTestHarmony.Patch(roomIconUpdate, transpiler: new HarmonyMethod(roomIconTranspiler));
}
finally
{
    roomIconTestHarmony.Unpatch(roomIconUpdate, HarmonyPatchType.All, roomIconTestHarmony.Id);
}

var hasMonsterCategory = skinServiceType.GetMethod("HasMonsterSkinCategory") ??
                         throw new InvalidOperationException("外观菜单无法判定怪物的分类优先级。");
var monsterCategories = (Dictionary<string, List<string>>)skinConfigType
    .GetProperty("MonsterSkinCategoryGroups")!.GetValue(testConfig)!;
monsterCategories["act:test"] = ["monster:test", "boss:test"];
foreach (var monsterGroup in new[] { "monster:test", "BOSS:TEST" })
{
    if (hasMonsterCategory.Invoke(null, new object[] { monsterGroup }) is not true)
        throw new InvalidOperationException("普通怪物与 Boss 均应支持跟随分类。");
}
if (hasMonsterCategory.Invoke(null, new object[] { "character:test" }) is not false)
    throw new InvalidOperationException("角色不应出现怪物跟随分类选项。");
monsterCategories.Remove("act:test");
var inheritMonsterId = (string)skinServiceType.GetField("InheritMonsterSelectionId")!.GetRawConstantValue()!;
var applyVisualSelection = skinServiceType.GetMethod("ApplySelection")!;
if (applyVisualSelection.Invoke(null, new object[] { "unregistered:monster", inheritMonsterId }) is not false ||
    skinServiceType.GetProperty("LastError")!.GetValue(null) as string !=
        "怪物 unregistered:monster 不属于已登记的图鉴分类。")
    throw new InvalidOperationException("跟随分类必须路由到地区优先级，不能作为一个皮肤素材 ID 应用。");

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

var cardRewardPreloadPatch = RequirePatchType(
    "STS2SkinChanger.Ui.CardRewardPortraitPreloadPatch",
    "缺少卡牌奖励批量卡图预载补丁；奖励牌会逐张扩展同一个隔离资源包并造成明显卡顿。");
var cardRewardPreloadTarget = (MethodBase?)RequirePatchMethod(
        cardRewardPreloadPatch,
        "TargetMethod")
    .Invoke(null, null);
if (cardRewardPreloadTarget?.DeclaringType != typeof(NCardRewardSelectionScreen) ||
    cardRewardPreloadTarget.Name != nameof(NCardRewardSelectionScreen.RefreshOptions))
{
    throw new InvalidOperationException(
        "卡牌奖励批量预载必须覆盖 NCardRewardSelectionScreen.RefreshOptions，包含首次打开和刷新奖励两条路径。");
}
var cardRewardPreloadPrefix = RequirePatchMethod(cardRewardPreloadPatch, "Prefix");
var cardRewardPreloadParameters = cardRewardPreloadPrefix.GetParameters();
if (cardRewardPreloadParameters.Length != 1 ||
    cardRewardPreloadParameters[0].ParameterType != typeof(IReadOnlyList<CardCreationResult>))
{
    throw new InvalidOperationException(
        "卡牌奖励批量预载必须一次接收整组 CardCreationResult，不能退回逐张建立隔离包。");
}

var skinCatalogType = typeof(Entry).Assembly.GetType("STS2SkinChanger.Catalog.SkinCatalog") ??
                      throw new InvalidOperationException("找不到皮肤目录类型。");
var skinOptionType = typeof(Entry).Assembly.GetType("STS2SkinChanger.Catalog.SkinOption") ??
                     throw new InvalidOperationException("找不到皮肤选项类型。");
var compositionSourcesProperty = skinOptionType.GetProperty(
    "CompositionSourceOptionIds",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
if (compositionSourcesProperty?.PropertyType != typeof(IReadOnlyList<string>))
{
    throw new InvalidOperationException(
        "虚拟合并皮肤必须保留有序原始来源，供头像、动态行为、本地化和多人同步共同解析。");
}
var compositionProvidersProperty = skinOptionType.GetProperty(
    "CompositionSourceProviderIds",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
if (compositionProvidersProperty?.PropertyType != typeof(IReadOnlyList<string>))
{
    throw new InvalidOperationException(
        "虚拟合并皮肤必须保留每个来源的提供者，避免私有依赖和共享遗物跨 Mod 串用。");
}

foreach (var methodName in new[]
         {
             "SynchronizeCharacterSkinCompositions",
             "GetRawCharacterOptions",
             "GetCompositionSourceOptionIds",
             "GetSelectionProviderIds",
             "TryCreateSessionCharacterComposition",
             "ClearSessionCharacterCompositions",
             "SelectionUsesVisualProvider"
         })
{
    if (skinCatalogType.GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) == null)
    {
        throw new InvalidOperationException($"皮肤目录缺少合并皮肤边界：{methodName}。");
    }
}

foreach (var methodName in new[]
         {
             "GetCharacterSkinOptions",
             "GetRawCharacterSkinOptions",
             "GetCharacterSkinCompositions",
             "SaveCharacterSkinComposition",
             "DeleteCharacterSkinComposition",
             "GetCharacterSelectionSourceIds",
             "TryBuildSessionCharacterComposition"
         })
{
    if (skinServiceType.GetMethod(
            methodName,
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) == null)
    {
        throw new InvalidOperationException($"皮肤服务缺少合并皮肤边界：{methodName}。");
    }
}

var buildSelectorMethod = contextualControlsType.GetMethod(
                              "BuildSelector",
                              BindingFlags.Static | BindingFlags.NonPublic) ??
                          throw new InvalidOperationException("找不到皮肤下拉框构建方法。");
if (buildSelectorMethod.GetParameters().Length != 0)
{
    throw new InvalidOperationException(
        "角色皮肤选择器不能再保留独立头像控件参数；头像必须与普通皮肤走同一选择。");
}

foreach (var removedMethodName in new[]
         {
             "GetCharacterIconSelection",
             "ApplyCharacterIconSelection"
         })
{
    if (skinServiceType.GetMethod(
            removedMethodName,
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) != null)
    {
        throw new InvalidOperationException(
            $"独立头像写入 API {removedMethodName} 仍然存在，会让头像与角色皮肤再次分叉。");
    }
}

foreach (var removedCatalogMethodName in new[]
         {
             "GetCharacterIconOptions",
             "IsCharacterIconOnlyOption",
             "CharacterIconOptionContainsResource"
         })
{
    if (skinCatalogType.GetMethod(
            removedCatalogMethodName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null)
    {
        throw new InvalidOperationException(
            $"皮肤目录仍暴露独立头像来源接口：{removedCatalogMethodName}；头像包必须只是普通角色皮肤。");
    }
}

var compositionControlsType = typeof(Entry).Assembly.GetType(
                                  "STS2SkinChanger.Ui.CharacterSkinCompositionControls") ??
                              throw new InvalidOperationException(
                                  "缺少选角界面的角色皮肤合并编辑器。");
var compositionShowMethod = compositionControlsType.GetMethod(
                                "Show",
                                BindingFlags.Static | BindingFlags.Public |
                                BindingFlags.NonPublic) ??
                            throw new InvalidOperationException(
                                "角色皮肤合并编辑器缺少 Show 入口。");
if (compositionShowMethod.GetParameters().Length != 3)
{
    throw new InvalidOperationException(
        "角色皮肤合并编辑器 Show 必须接收选角界面、当前角色分组和刷新回调。");
}

var modTextType = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.ModText") ??
                  throw new InvalidOperationException("找不到 ModText。");
var compositionEditorStateType = compositionControlsType.GetNestedType("EditorState", BindingFlags.NonPublic)!;
var compositionEditorState = Activator.CreateInstance(compositionEditorStateType, new object?[] { null, null, null, null })!;
var confirmCompositionDelete = compositionEditorStateType.GetMethod("TryConfirmDelete") ??
                               throw new InvalidOperationException("合并皮肤删除缺少二次确认。");
var editingCompositionId = compositionEditorStateType.GetProperty("EditingCompositionId")!;
editingCompositionId.SetValue(compositionEditorState, "composition:first");
if ((bool)confirmCompositionDelete.Invoke(compositionEditorState, null)!)
    throw new InvalidOperationException("首次点击删除不能直接删除合并皮肤。");
if (!(bool)confirmCompositionDelete.Invoke(compositionEditorState, null)!)
    throw new InvalidOperationException("第二次点击删除应确认当前合并皮肤。");
if ((bool)confirmCompositionDelete.Invoke(compositionEditorState, null)!)
    throw new InvalidOperationException("删除确认只能使用一次。");
editingCompositionId.SetValue(compositionEditorState, "composition:second");
if ((bool)confirmCompositionDelete.Invoke(compositionEditorState, null)!)
    throw new InvalidOperationException("切换合并皮肤后不能沿用旧的删除确认。");
compositionControlsType.GetMethod("ResetDraft", BindingFlags.Static | BindingFlags.NonPublic)!
    .Invoke(null, new[] { compositionEditorState });
editingCompositionId.SetValue(compositionEditorState, "composition:second");
if ((bool)confirmCompositionDelete.Invoke(compositionEditorState, null)!)
    throw new InvalidOperationException("重开编辑器后必须重新确认删除。");
var compositionTextNames = new[]
{
    "CharacterSkinMerge",
    "NewCharacterSkinMerge",
    "CharacterSkinMergeName",
    "HideMergedSkinSources",
    "SaveCharacterSkinMerge",
    "DeleteCharacterSkinMerge",
    "ConfirmDeleteCharacterSkinMerge",
    "CharacterSkinSourceUnavailable",
    "CharacterSkinMergeNeedsSource"
};
foreach (var textName in compositionTextNames)
{
    if (!Enum.GetNames(modTextType).Contains(textName, StringComparer.Ordinal))
    {
        throw new InvalidOperationException($"角色皮肤合并缺少本地化键：{textName}。");
    }
}

if (Enum.GetNames(modTextType).Contains("CharacterIcon", StringComparer.Ordinal) ||
    Enum.GetNames(modTextType).Contains("FollowCharacterSkin", StringComparer.Ordinal))
{
    throw new InvalidOperationException("独立头像文本仍然存在，头像尚未完全并入角色皮肤。");
}

var modLocalizationType = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.ModLocalization") ??
                          throw new InvalidOperationException("找不到 ModLocalization。");
var compositionPacksField = modLocalizationType.GetField(
                                "CharacterSkinCompositionPacks",
                                BindingFlags.Static | BindingFlags.NonPublic) ??
                            throw new InvalidOperationException(
                                "缺少角色皮肤合并的多语言文本包。");
var compositionPacks = compositionPacksField.GetValue(null) as System.Collections.IDictionary ??
                       throw new InvalidOperationException(
                           "角色皮肤合并的多语言文本包类型无效。");
if (compositionPacks.Count != 15)
{
    throw new InvalidOperationException(
        $"角色皮肤合并必须覆盖 15 种语言，当前只有 {compositionPacks.Count} 种。");
}

foreach (System.Collections.DictionaryEntry pack in compositionPacks)
{
    var values = pack.Value!.GetType().GetProperties()
        .Where(property => property.PropertyType == typeof(string))
        .Select(property => property.GetValue(pack.Value) as string)
        .ToArray();
    if (values.Length < compositionTextNames.Length ||
        values.Any(string.IsNullOrWhiteSpace))
    {
        throw new InvalidOperationException(
            $"角色皮肤合并语言 {pack.Key} 存在空白或缺失文本。");
    }
}

Console.WriteLine("Skin Changer runtime patch target tests passed.");
RequiredLibraryVisualGuardTests.Run(args);

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
