using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using STS2SkinChanger;
using Godot;
using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using STS2SkinChanger.Core;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

internal static class MultiplayerAppearanceIdentityTests
{
    private static Player? _localPlayer;
    private static bool NoSave(ref SaveManager? __result) { __result = null; return false; }
    private static bool LocalPlayer(ref Player? __result) { __result = _localPlayer; return false; }
    private static bool SkipEngine() => false;
    private static int _mountCalls;
    private static int _iconFailures;
    private static bool _appearanceReady;
    private static bool AllowApply(ref bool __result) { __result = true; return false; }
    private static bool Mount()
    {
        if (++_mountCalls == 1) throw new IOException("fixture: resource temporarily unavailable");
        return false;
    }
    private static bool Icons(ref bool __result)
    {
        if (_iconFailures-- > 0) throw new InvalidOperationException("fixture: avatar not ready");
        __result = _appearanceReady;
        return false;
    }
    private static bool Appearance(ref bool __result) { __result = _appearanceReady; return false; }
    private static NCombatRoom? _combatRoom;
    private static NCreature? _failedCreature;
    private static bool CombatRoom(ref NCombatRoom? __result) { __result = _combatRoom; return false; }
    private static bool Rebuild(NCreature creature, ref string? error, ref bool __result)
    {
        __result = !ReferenceEquals(creature, _failedCreature);
        error = __result ? null : "fixture: skeleton not ready";
        return false;
    }
    private static readonly List<string> HandRequests = [];
    private static bool HandTexture(MethodBase __originalMethod, ref Texture2D __result)
    {
        HandRequests.Add(__originalMethod.Name + ":" +
            (MultiplayerSkinSync.GetScopedSelection("defect") ?? SkinService.Config.GetSelection("defect")));
        __result = null!;
        return false;
    }

    internal static void Run(bool testRetries = false, bool testHands = false)
    {
        var assembly = typeof(Entry).Assembly;
        Type Type(string name) => assembly.GetType("STS2SkinChanger." + name, true)!;
        var sync = Type("Core.MultiplayerSkinSync");
        var service = Type("Core.SkinService");
        var preview = Type("Core.FrameworkModelPreview");
        var configType = Type("Core.SkinConfig");
        var configProperty = AccessTools.Property(service, "Config");
        var catalogProperty = AccessTools.Property(service, "Catalog");
        var previousConfig = configProperty.GetValue(null);
        var previousCatalog = catalogProperty.GetValue(null);
        var fields = new[] { "_netService", "_lobby", "_inRun", "_snapshotStage", "_snapshotElapsed",
            "_needsLobbyRoundReset", "_suspendedLobby", "_resumeRunWhenEnabled", "_runtimeProvidersDirty", "_appearanceRetryCooldown" };
        var previous = fields.ToDictionary(name => name, name => AccessTools.Field(sync, name).GetValue(null));
        var configLoaded = AccessTools.Field(service, "_configLoaded");
        var wasLoaded = configLoaded.GetValue(null);
        var runNetService = RunManager.Instance.NetService;
        var harmony = new Harmony("tests.multiplayer-appearance-identity");
        var advertised = (IDictionary)AccessTools.Field(sync, "AdvertisedSelections").GetValue(null)!;
        var capable = (HashSet<ulong>)AccessTools.Field(sync, "CapablePeers").GetValue(null)!;
        var oldAds = advertised.Cast<DictionaryEntry>().ToArray();
        var oldPeers = capable.ToArray();
        var stateCollections = new[] { "AvailableSelections", "PendingRefreshes", "PendingTransformRefreshes", "PendingIconRefreshes",
            "LocalFallbackSelections", "LocalFallbackTransforms", "LastAppliedTransformSignatures", "LastReceivedTransformSignatures" }
            .Select(name => AccessTools.Field(sync, name).GetValue(null)!).ToArray();
        var oldCollectionItems = stateCollections.Select(collection => ((IEnumerable)collection).Cast<object>().ToArray()).ToArray();
        try
        {
            harmony.Patch(AccessTools.PropertyGetter(typeof(SaveManager), "Instance"),
                prefix: new HarmonyMethod(typeof(MultiplayerAppearanceIdentityTests), nameof(NoSave)));
            harmony.Patch(AccessTools.Method(Type("Ui.CharacterAppearanceRuntime"), "GetLocalPlayer"),
                prefix: new HarmonyMethod(typeof(MultiplayerAppearanceIdentityTests), nameof(LocalPlayer)));
            foreach (var method in Type("Core.ModLog").GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                         .Where(m => m.Name is "Info" or "Warn" or "Error"))
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(MultiplayerAppearanceIdentityTests), nameof(SkipEngine)));

            var config = JsonSerializer.Deserialize("""
                {"MultiplayerSkinSyncEnabled":true,"LoadOtherPlayersCustomSkins":true,
                 "Selections":{"necrobinder":"knight","defect":"pea","ironclad":"__base__"}}
                """, configType)!;
            configProperty.SetValue(null, config);
            configLoaded.SetValue(null, true);
            // No catalog is needed to prove the incorrect remote-player fallback.
            catalogProperty.SetValue(null, null);
            var host = DispatchProxy.Create<INetHostGameService, AppearanceHostProxy>();
            var transport = (AppearanceHostProxy)(object)host;
            AccessTools.Field(sync, "_netService").SetValue(null, host);
            advertised.Clear(); capable.Clear(); capable.Add(202UL);
            var knightCharacter = Character<Necrobinder>("NECROBINDER");
            var previewPlayer = (Player)AccessTools.Method(preview, "CreatePreviewPlayer")
                .Invoke(null, [knightCharacter, null])!;
            var resolve = AccessTools.Method(sync, "GetSelectionForCreature");
            Require((string)resolve.Invoke(null, [previewPlayer.Creature, "necrobinder"])! == "knight",
                "联机小预览被误判为未同步的远端玩家，所选小骑士退回原皮。");
            using (var scope = (IDisposable?)AccessTools.Method(sync, "BeginCreatureSelectionScope")
                       .Invoke(null, [previewPlayer.Creature]))
                Require(scope == null, "菜单临时角色不能建立远端原皮覆盖作用域。");
            // The same numerical ID can be a real ENet player: do not exempt magic ID zero.
            var remote = PlayerWithId(previewPlayer, previewPlayer.NetId);
            Require((string)resolve.Invoke(null, [remote.Creature, "necrobinder"])! == "__base__",
                "真正未同步的远端玩家仍必须使用原皮，不能继承本机小骑士。");

            catalogProperty.SetValue(null, CreateCatalog(assembly, catalogProperty.PropertyType));
            var lobby = (StartRunLobby)RuntimeHelpers.GetUninitializedObject(typeof(StartRunLobby));
            AccessTools.Field(typeof(StartRunLobby), "<NetService>k__BackingField").SetValue(lobby, host);
            var playerListProperty = AccessTools.Property(typeof(StartRunLobby), "Players");
            var players = (IList)Activator.CreateInstance(playerListProperty.PropertyType)!;
            AccessTools.Field(typeof(StartRunLobby), "<Players>k__BackingField").SetValue(lobby, players);
            var listener = DispatchProxy.Create<IStartRunLobbyListener, AppearanceHostProxy>();
            AccessTools.Field(typeof(StartRunLobby), "<LobbyListener>k__BackingField").SetValue(lobby, listener);
            var lobbyPlayer = Activator.CreateInstance(playerListProperty.PropertyType.GenericTypeArguments[0])!;
            AccessTools.Field(lobbyPlayer.GetType(), "id").SetValue(lobbyPlayer, 101UL);
            AccessTools.Field(lobbyPlayer.GetType(), "character").SetValue(lobbyPlayer, Character<Ironclad>("IRONCLAD"));
            players.Add(lobbyPlayer);
            AccessTools.Field(sync, "_lobby").SetValue(null, lobby);
            AccessTools.Method(sync, "RememberLocalAdvertisement").Invoke(null, [false]);

            var patch = assembly.GetType("STS2SkinChanger.Core.MultiplayerLobbyCharacterChangedPatch");
            if (patch != null) harmony.CreateClassProcessor(patch).Patch();
            var change = AccessTools.Method(typeof(StartRunLobby), "ChangeCharacter");
            change.Invoke(lobby, [101UL, Character<RandomCharacter>("RANDOM_CHARACTER"), false]);
            Require(!advertised.Contains(101UL), "随机尚未确定时不能继续发送上一个角色的皮肤。");
            transport.Messages.Clear();
            var robot = Character<Defect>("DEFECT");
            change.Invoke(lobby, [101UL, robot, true]);
            Require(transport.Messages.Any(message => MessageValue(message, "PlayerNetId") is 101UL &&
                    MessageValue(message, "CharacterId") is "DEFECT" && MessageValue(message, "OptionId") is "pea"),
                "随机角色确定后必须立即发送最终角色及本机选择，而不是旧的铁甲战士/原皮。");
            Require(transport.Messages.All(message => MessageValue(message, "SourceOptionManifest") is "[\"pea\"]"),
                "必须发送实际安装来源，不能只发送界面名称或空来源。");
            var count = transport.Messages.Count;
            change.Invoke(lobby, [202UL, robot, true]);
            Require(transport.Messages.Count == count, "不能代替对方广播本机给该角色配置的皮肤。");

            // Replay a real generated payload through the receiver. A packet may precede the
            // receiver's own random resolution; it must be applied once that identity is known.
            var peerRow = Activator.CreateInstance(lobbyPlayer.GetType())!;
            AccessTools.Field(peerRow.GetType(), "id").SetValue(peerRow, 202UL);
            AccessTools.Field(peerRow.GetType(), "character").SetValue(peerRow, Character<RandomCharacter>("RANDOM_CHARACTER"));
            players.Add(peerRow);
            var packet = transport.Messages[0];
            AccessTools.Field(packet.GetType(), "PlayerNetId").SetValue(packet, 202UL);
            AccessTools.Method(sync, "HandleMessage").Invoke(null, [packet, 202UL]);
            var available = (IDictionary)AccessTools.Field(sync, "AvailableSelections").GetValue(null)!;
            Require(!available.Contains(202UL), "尚未确定的随机角色不能提前套用另一角色模型。");
            change.Invoke(lobby, [202UL, robot, true]);
            AccessTools.Method(sync, "RetryAdvertisementsWaitingForPlayerIdentity").Invoke(null, null);
            var remoteRobot = PlayerWithId(previewPlayer, 202UL, robot);
            ((IDictionary)AccessTools.Property(configType, "Selections").GetValue(config)!).Remove("defect");
            Require((string)resolve.Invoke(null, [remoteRobot.Creature, "defect"])! == "pea" &&
                    AccessTools.Method(sync, "CanEditSkinForCreature").Invoke(null, [remoteRobot.Creature]) is false,
                $"本机已安装的对方皮肤应按玩家隔离加载并锁定；实际={resolve.Invoke(null, [remoteRobot.Creature, "defect"])}，" +
                $"收到={advertised.Contains(202UL)}，可用={available[202UL]}。");
            ((IDictionary)AccessTools.Property(configType, "Selections").GetValue(config)!)["defect"] = "pea";

            if (testHands)
            {
                var handReady = AccessTools.Method(Type("Core.MultiplayerTreasureHandReadyScopePatch"), "Postfix");
                Require(handReady != null, "宝箱手部创建后未登记，迟到的玩家外观无法刷新已有手部。");
                foreach (var name in new[] { "ArmPointingTexture", "ArmRockTexture", "ArmPaperTexture", "ArmScissorsTexture" })
                    harmony.Patch(AccessTools.PropertyGetter(typeof(CharacterModel), name),
                        prefix: new HarmonyMethod(typeof(MultiplayerAppearanceIdentityTests), nameof(HandTexture)));
                harmony.Patch(AccessTools.PropertySetter(typeof(TextureRect), "Texture"),
                    prefix: new HarmonyMethod(typeof(MultiplayerAppearanceIdentityTests), nameof(SkipEngine)));
                harmony.Patch(AccessTools.Method(typeof(GodotObject), "IsInstanceValid"),
                    prefix: new HarmonyMethod(typeof(MultiplayerAppearanceIdentityTests), nameof(AllowApply)));
                harmony.Patch(AccessTools.Method(typeof(Node), "IsInsideTree"),
                    prefix: new HarmonyMethod(typeof(MultiplayerAppearanceIdentityTests), nameof(AllowApply)));
                harmony.CreateClassProcessor(Type("Core.MultiplayerTreasureHandMoveScopePatch")).Patch();
                var hand = (NHandImage)RuntimeHelpers.GetUninitializedObject(typeof(NHandImage));
                var other = (NHandImage)RuntimeHelpers.GetUninitializedObject(typeof(NHandImage));
                foreach (var (node, player) in new[] { (hand, remoteRobot), (other, PlayerWithId(previewPlayer, 303UL, robot)) })
                {
                    AccessTools.Field(typeof(NHandImage), "<Player>k__BackingField").SetValue(node, player);
                    AccessTools.Field(typeof(NHandImage), "_textureRect").SetValue(node,
                        RuntimeHelpers.GetUninitializedObject(typeof(TextureRect)));
                    handReady!.Invoke(null, [node]);
                }
                var refresh = AccessTools.Method(Type("Core.MultiplayerTreasureHandAppearance"), "RefreshPlayer");
                var move = AccessTools.Method(typeof(NHandImage), "SetTextureToFightMove");
                try
                {
                    HandRequests.Clear();
                    Require(refresh.Invoke(null, [202UL]) is true && HandRequests.SequenceEqual(["get_ArmPointingTexture:pea"]),
                        "迟到外观必须只刷新指定玩家的指向手部。");
                    foreach (var (pose, getter) in new[] { (RelicPickingFightMove.Rock, "ArmRockTexture"),
                                 (RelicPickingFightMove.Paper, "ArmPaperTexture"), (RelicPickingFightMove.Scissors, "ArmScissorsTexture") })
                    {
                        move.Invoke(hand, [pose]);
                        HandRequests.Clear();
                        refresh.Invoke(null, [202UL]);
                        Require(HandRequests.SequenceEqual([$"get_{getter}:pea"]),
                            "刷新皮肤必须保留猜拳/抓遗物当前姿势，不能回到指向状态。");
                    }
                    available.Remove(202UL);
                    HandRequests.Clear();
                    refresh.Invoke(null, [202UL]);
                    Require(HandRequests.SequenceEqual(["get_ArmScissorsTexture:__base__"]),
                        "对方皮肤不可用时手部也应回退原皮，不能保留上次皮肤。");
                }
                finally
                {
                    var remove = AccessTools.Method(Type("Core.MultiplayerTreasureHandExitPatch"), "Postfix");
                    remove.Invoke(null, [hand]); remove.Invoke(null, [other]);
                }
                HandRequests.Clear();
                Require(refresh.Invoke(null, [202UL]) is false && HandRequests.Count == 0,
                    "退出宝箱后不能刷新已退出的手部节点。");
                return;
            }

            if (testRetries)
            {
                var runtime = Type("Ui.CharacterAppearanceRuntime");
                harmony.Patch(AccessTools.Method(runtime, "CanApplySelectionImmediately"),
                    prefix: new HarmonyMethod(typeof(MultiplayerAppearanceIdentityTests), nameof(AllowApply)));
                harmony.Patch(AccessTools.Method(service, "RefreshSessionRuntimeProviders"),
                    prefix: new HarmonyMethod(typeof(MultiplayerAppearanceIdentityTests), nameof(Mount)));
                harmony.Patch(AccessTools.Method(Type("Ui.ContextualSkinControls"), "RefreshMultiplayerPlayerIcons"),
                    prefix: new HarmonyMethod(typeof(MultiplayerAppearanceIdentityTests), nameof(Icons)));
                var refresh = AccessTools.Method(runtime, "RefreshPlayerAppearance");
                harmony.Patch(refresh, prefix: new HarmonyMethod(typeof(MultiplayerAppearanceIdentityTests),
                    refresh.ReturnType == typeof(bool) ? nameof(Appearance) : nameof(SkipEngine)));
                _mountCalls = 0; _iconFailures = 1; _appearanceReady = false;
                AccessTools.Field(sync, "_appearanceRetryCooldown").SetValue(null, 0d);
                AccessTools.Field(sync, "_snapshotStage").SetValue(null, 2);
                var pending = (HashSet<ulong>)AccessTools.Field(sync, "PendingRefreshes").GetValue(null)!;
                var pendingIcons = (HashSet<ulong>)AccessTools.Field(sync, "PendingIconRefreshes").GetValue(null)!;
                var tick = AccessTools.Method(sync, "Tick");
                tick.Invoke(null, [0.01d]);
                Require(AccessTools.Field(sync, "_runtimeProvidersDirty").GetValue(null) is true && pending.Contains(202UL),
                    "资源挂载失败后丢弃了 dirty 标志或玩家刷新请求。");
                tick.Invoke(null, [0.01d]);
                Require(_mountCalls == 1, "失败重试不能每帧重建资源，造成卡顿。");
                tick.Invoke(null, [2.1d]);
                Require(_mountCalls == 2 && pending.Contains(202UL) && pendingIcons.Contains(202UL),
                    "模型未就绪/头像异常时不能把刷新请求误报完成。");
                _appearanceReady = true;
                tick.Invoke(null, [2.1d]);
                Require(!pending.Contains(202UL) && !pendingIcons.Contains(202UL) && _mountCalls == 2,
                    "重试成功应清除对应请求，不能反复重建已挂载资源。");

                // Keep the completion decision real; replace only engine-backed reconstruction.
                harmony.Unpatch(refresh, HarmonyPatchType.Prefix, harmony.Id);
                harmony.Patch(AccessTools.PropertyGetter(typeof(NCombatRoom), "Instance"),
                    prefix: new HarmonyMethod(typeof(MultiplayerAppearanceIdentityTests), nameof(CombatRoom)));
                harmony.Patch(AccessTools.Method(runtime, "TryRebuildCreatureVisuals"),
                    prefix: new HarmonyMethod(typeof(MultiplayerAppearanceIdentityTests), nameof(Rebuild)));
                harmony.Patch(AccessTools.Method(runtime, "RefreshPlayerAndPetLayout"),
                    prefix: new HarmonyMethod(typeof(MultiplayerAppearanceIdentityTests), nameof(SkipEngine)));
                _combatRoom = null;
                Require(refresh.Invoke(null, [202UL]) is false, "战斗节点尚未创建不能标记模型刷新成功。");
                _combatRoom = (NCombatRoom)RuntimeHelpers.GetUninitializedObject(typeof(NCombatRoom));
                var creatures = new List<NCreature>();
                AccessTools.Field(typeof(NCombatRoom), "_creatureNodes").SetValue(_combatRoom, creatures);
                Require(refresh.Invoke(null, [202UL]) is false, "房间存在但玩家尚未加入也不能消费请求。");
                for (var i = 0; i < 2; i++)
                {
                    var node = (NCreature)RuntimeHelpers.GetUninitializedObject(typeof(NCreature));
                    AccessTools.Field(typeof(NCreature), "<Entity>k__BackingField").SetValue(node, remoteRobot.Creature);
                    creatures.Add(node);
                }
                _failedCreature = creatures[1];
                Require(refresh.Invoke(null, [202UL]) is false, "同一玩家部分模型失败时不能标记全部成功。");
                _failedCreature = null;
                Require(refresh.Invoke(null, [202UL]) is true, "全部模型成功刷新后应允许移除请求。");
                _combatRoom = null;
                return;
            }

            // Same connected transport, but the lobby's two startup snapshots ran long ago.
            _localPlayer = PlayerWithId(previewPlayer, 101UL, robot);
            AccessTools.PropertySetter(typeof(RunManager), "NetService").Invoke(RunManager.Instance, [host]);
            AccessTools.Field(sync, "_inRun").SetValue(null, false);
            AccessTools.Field(sync, "_snapshotStage").SetValue(null, 2);
            AccessTools.Field(sync, "_snapshotElapsed").SetValue(null, 120d);
            transport.Messages.Clear();
            AccessTools.Method(sync, "AttachToRun").Invoke(null, null);
            Require(transport.Messages.Any(message => MessageValue(message, "CharacterId") is "DEFECT" &&
                    MessageValue(message, "OptionId") is "pea"), "从大厅进入对局必须重新发送最终外观，不能只更新本机缓存。");
            Require((int)AccessTools.Field(sync, "_snapshotStage").GetValue(null)! == 0 &&
                    (double)AccessTools.Field(sync, "_snapshotElapsed").GetValue(null)! == 0,
                "进入新阶段须重新安排延迟快照，以覆盖角色/网络节点尚未就绪的情况。");
            AccessTools.Property(configType, "MultiplayerSkinSyncEnabled").SetValue(config, false);
            transport.Messages.Clear();
            AccessTools.Field(sync, "_inRun").SetValue(null, false);
            AccessTools.Method(sync, "AttachToRun").Invoke(null, null);
            Require(transport.Messages.Count == 0, "关闭发送后，即使开局也不应广播自己的外观。");
            AccessTools.Property(configType, "MultiplayerSkinSyncEnabled").SetValue(config, true);
            transport.ThrowOnSend = true;
            AccessTools.Field(sync, "_inRun").SetValue(null, false);
            AccessTools.Method(sync, "AttachToRun").Invoke(null, null);
            Require((int)AccessTools.Field(sync, "_snapshotStage").GetValue(null)! == 0,
                "发送失败不能阻断开局，必须保留后续快照重试。");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            configProperty.SetValue(null, previousConfig);
            catalogProperty.SetValue(null, previousCatalog);
            configLoaded.SetValue(null, wasLoaded);
            foreach (var (name, value) in previous) AccessTools.Field(sync, name).SetValue(null, value);
            advertised.Clear(); foreach (var pair in oldAds) advertised.Add(pair.Key, pair.Value);
            capable.Clear(); capable.UnionWith(oldPeers);
            for (var i = 0; i < stateCollections.Length; i++)
            {
                var collection = stateCollections[i];
                collection.GetType().GetMethod("Clear")!.Invoke(collection, null);
                foreach (var item in oldCollectionItems[i])
                {
                    if (collection is IDictionary dictionary)
                    {
                        var key = item.GetType().GetProperty("Key")!.GetValue(item)!;
                        dictionary.Add(key, item.GetType().GetProperty("Value")!.GetValue(item));
                    }
                    else collection.GetType().GetMethod("Add")!.Invoke(collection, [item]);
                }
            }
            AccessTools.PropertySetter(typeof(RunManager), "NetService").Invoke(RunManager.Instance, [runNetService]);
            _localPlayer = null;
        }
        Console.WriteLine("Multiplayer appearance identity passed: preview isolation, random resolution, deferred receive and phase snapshots.");
    }

    private static object? MessageValue(object message, string field) => AccessTools.Field(message.GetType(), field).GetValue(message);
    private static T Character<T>(string id) where T : CharacterModel
    {
        var character = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        AccessTools.Field(typeof(AbstractModel), "<Id>k__BackingField").SetValue(character, new ModelId("CHARACTER", id));
        return character;
    }
    private static Player PlayerWithId(Player template, ulong id, CharacterModel? character = null)
    {
        var constructor = typeof(Player).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(c => c.GetParameters().Length == 15);
        character ??= template.Character;
        return (Player)constructor.Invoke([character, id, character.StartingHp, character.StartingHp,
            character.MaxEnergy, 0, 0, character.BaseOrbSlotCount, new RelicGrabBag(), null, null, null, null, null, null]);
    }
    private static object CreateCatalog(Assembly assembly, Type catalogType)
    {
        var catalog = RuntimeHelpers.GetUninitializedObject(catalogType);
        var groupType = assembly.GetType("STS2SkinChanger.Catalog.SkinGroup", true)!;
        var optionType = assembly.GetType("STS2SkinChanger.Catalog.SkinOption", true)!;
        var groups = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(groupType))!;
        foreach (var (id, optionId) in new[] { ("ironclad", "__base__"), ("defect", "pea") })
        {
            var group = Activator.CreateInstance(groupType, [id, id])!;
            var constructor = optionType.GetConstructors().Single(c => c.GetParameters().Length > 2);
            var args = constructor.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue :
                p.ParameterType.IsGenericType ? Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(p.ParameterType.GenericTypeArguments)) : null).ToArray();
            args[0] = optionId; args[1] = optionId;
            args[3] = true; // A registered character runtime provider, not an empty non-skin asset bundle.
            ((IList)groupType.GetProperty("Options")!.GetValue(group)!).Add(constructor.Invoke(args));
            groups.Add(group);
        }
        AccessTools.Field(catalogType, "_groups").SetValue(catalog, groups);
        AccessTools.Field(catalogType, "_characterAppearanceGroupIds").SetValue(catalog, new HashSet<string> { "ironclad", "defect" });
        AccessTools.Field(catalogType, "_fullRuntimeProviders").SetValue(catalog, new HashSet<string>());
        AccessTools.Field(catalogType, "_fullRuntimeProviderGroups").SetValue(catalog, new Dictionary<string, IReadOnlyList<string>>());
        return catalog;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

public class AppearanceHostProxy : DispatchProxy
{
    public List<object> Messages { get; } = [];
    public bool ThrowOnSend { get; set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case "get_Type": return NetGameType.Host;
            case "get_NetId": return 101UL;
            case "get_IsConnected": return true;
            case "SendMessage":
                if (ThrowOnSend) throw new IOException("Test transport unavailable");
                Messages.Add(args![0]!); return null;
            default: return method.ReturnType == typeof(void) ? null :
                method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
        }
    }
}
