using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Runs;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Ui;

namespace STS2SkinChanger.Core;

internal enum SkinSyncMessageKind : byte
{
    CharacterSelection = 1,
    ReadyResolutionComplete = 2,
    ReadyResolutionProbe = 3
}

/// <summary>
/// One stable envelope is deliberately used for every Skin Changer protocol message. The game's
/// mod message IDs are normally assigned by sorting all discovered message type names, which is
/// not stable when two peers have different cosmetic mods. Harmony reserves one high byte for
/// this envelope so unrelated mod message types cannot shift it.
/// </summary>
internal struct SkinChangerNetMessage : INetMessage
{
    public byte ProtocolVersion;
    public SkinSyncMessageKind Kind;
    public ulong PlayerNetId;
    public string CharacterId;
    public string GroupId;
    public string OptionId;
    public string ProviderId;
    public ulong WorkshopItemId;
    public string SafeResourceFingerprint;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteByte(ProtocolVersion);
        writer.WriteByte((byte)Kind);
        writer.WriteULong(PlayerNetId);
        writer.WriteString(CharacterId ?? string.Empty);
        writer.WriteString(GroupId ?? string.Empty);
        writer.WriteString(OptionId ?? string.Empty);
        writer.WriteString(ProviderId ?? string.Empty);
        writer.WriteULong(WorkshopItemId);
        writer.WriteString(SafeResourceFingerprint ?? string.Empty);
    }

    public void Deserialize(PacketReader reader)
    {
        ProtocolVersion = reader.ReadByte();
        Kind = (SkinSyncMessageKind)reader.ReadByte();
        PlayerNetId = reader.ReadULong();
        CharacterId = reader.ReadString();
        GroupId = reader.ReadString();
        OptionId = reader.ReadString();
        ProviderId = reader.ReadString();
        WorkshopItemId = reader.ReadULong();
        SafeResourceFingerprint = reader.ReadString();
    }
}

internal sealed record SessionCharacterSelection(
    string CharacterId,
    string GroupId,
    string OptionId,
    IReadOnlyDictionary<string, string> SelectionOverrides);

internal static class MultiplayerSkinSync
{
    internal const byte ProtocolVersion = 4;
    internal const int ReservedMessageId = 254;
    private const double ReadyGateQuietSeconds = 0.75;
    private const double ReadyGateTimeoutSeconds = 180.0;

    private static readonly byte[] CapabilityMagic =
        [0x47, 0x53, 0x43, 0x41, 0x50, 0x30, 0x34, 0x21]; // GSCAP04!
    private static readonly HashSet<ulong> CapablePeers = [];
    private static readonly Dictionary<ulong, SkinChangerNetMessage> AdvertisedSelections = [];
    private static readonly Dictionary<ulong, SessionCharacterSelection> AvailableSelections = [];
    private static readonly HashSet<ulong> PendingRefreshes = [];
    private static readonly HashSet<ulong> ReadyResolutionCompletePeers = [];
    private static readonly object Sync = new();

    [ThreadStatic]
    private static Stack<IReadOnlyDictionary<string, string>>? _selectionScopes;

    private static INetGameService? _netService;
    private static StartRunLobby? _lobby;
    private static MessageHandlerDelegate<SkinChangerNetMessage>? _messageHandler;
    private static double _snapshotElapsed;
    private static int _snapshotStage;
    private static bool _runtimeProvidersDirty;
    private static bool _readyGateActive;
    private static bool _localReadyResolutionComplete;
    private static bool _runReleaseCommitted;
    private static double _readyGateElapsed;
    private static double _readyGateQuietElapsed;
    private static ulong _readyGateRevision;

    internal static string? GetScopedSelection(string groupId)
    {
        var scopes = _selectionScopes;
        if (scopes == null || scopes.Count == 0)
        {
            return null;
        }

        return scopes.Peek().GetValueOrDefault(groupId);
    }

    internal static IReadOnlyDictionary<string, string>? GetScopedSelections()
    {
        var scopes = _selectionScopes;
        return scopes is { Count: > 0 } ? scopes.Peek() : null;
    }

    internal static IReadOnlyList<IReadOnlyDictionary<string, string>> GetAvailableSelectionMaps()
    {
        lock (Sync)
        {
            return AvailableSelections.Values
                .Select(selection => selection.SelectionOverrides)
                .ToArray();
        }
    }

    internal static string GetSelectionForCreature(Creature creature, string groupId)
    {
        var player = creature.Player ?? creature.PetOwner;
        if (player == null)
        {
            return SkinService.Config.GetSelection(groupId);
        }

        lock (Sync)
        {
            if (AvailableSelections.TryGetValue(player.NetId, out var selection) &&
                selection.SelectionOverrides.TryGetValue(groupId, out var optionId))
            {
                return optionId;
            }
        }

        return SkinService.Config.GetSelection(groupId);
    }

    internal static IDisposable? BeginCreatureSelectionScope(Creature creature)
    {
        var player = creature.Player ?? creature.PetOwner;
        if (player == null)
        {
            return null;
        }

        IReadOnlyDictionary<string, string>? selections = null;
        lock (Sync)
        {
            if (AvailableSelections.TryGetValue(player.NetId, out var selection) &&
                selection.CharacterId.Equals(
                    player.Character.Id.Entry,
                    StringComparison.OrdinalIgnoreCase) &&
                ContextualSkinControls.FindGroup(
                    player.Character.Id.Entry,
                    player.Character.GetType().Name) is { } playerGroup &&
                playerGroup.Id.Equals(selection.GroupId, StringComparison.OrdinalIgnoreCase))
            {
                selections = selection.SelectionOverrides;
            }
        }

        if (selections == null)
        {
            return null;
        }

        _selectionScopes ??= new Stack<IReadOnlyDictionary<string, string>>();
        _selectionScopes.Push(selections);
        return new SelectionScope();
    }

    internal static void AttachToRun()
    {
        var service = RunManager.Instance.NetService;
        _lobby = null;
        if (!service.Type.IsMultiplayer())
        {
            return;
        }

        AttachToService(service, "对局");
        RememberLocalAdvertisement(includeOnlineMetadata: true);
    }

    internal static void AttachToLobby(StartRunLobby lobby)
    {
        var service = lobby.NetService;
        if (!service.Type.IsMultiplayer())
        {
            return;
        }

        var changedLobby = !ReferenceEquals(_lobby, lobby);
        AttachToService(service, "联机选角");
        _lobby = lobby;
        if (changedLobby)
        {
            ResetReadyGateState();
        }
        RememberLocalAdvertisement();
    }

    private static void AttachToService(INetGameService service, string stage)
    {
        if (ReferenceEquals(service, _netService))
        {
            return;
        }

        DetachFromRun(clearCapabilities: false);
        OnlineSkinCache.BeginSession();
        _netService = service;
        _messageHandler = HandleMessage;
        service.RegisterMessageHandler(_messageHandler);
        service.Disconnected += OnDisconnected;
        if (service is INetHostGameService host)
        {
            host.ClientDisconnected += OnClientDisconnected;
        }

        _snapshotElapsed = 0;
        _snapshotStage = 0;
        ModLog.Info($"多人角色皮肤同步已在{stage}阶段启用；仅向确认安装 Skin Changer 的玩家发送选择。");
    }

    internal static void DetachFromRun(
        bool clearCapabilities = true,
        bool refreshRuntimeProviders = false)
    {
        var service = _netService;
        if (service != null)
        {
            try
            {
                if (_messageHandler != null)
                {
                    service.UnregisterMessageHandler(_messageHandler);
                }
                service.Disconnected -= OnDisconnected;
                if (service is INetHostGameService host)
                {
                    host.ClientDisconnected -= OnClientDisconnected;
                }
            }
            catch (Exception exception)
            {
                ModLog.Warn("清理多人皮肤同步监听失败：" + exception.GetBaseException().Message);
            }
        }

        _netService = null;
        _lobby = null;
        _messageHandler = null;
        bool hadRemoteSelections;
        lock (Sync)
        {
            hadRemoteSelections = AvailableSelections.Count > 0;
            AdvertisedSelections.Clear();
            AvailableSelections.Clear();
            PendingRefreshes.Clear();
            ReadyResolutionCompletePeers.Clear();
            _runtimeProvidersDirty = false;
            if (clearCapabilities)
            {
                CapablePeers.Clear();
            }
        }

        ResetReadyGateState();

        if (hadRemoteSelections && refreshRuntimeProviders)
        {
            try
            {
                // Remote-only DLL providers must not leak into the next lobby or single-player
                // run after their per-player selections have been discarded.
                SkinService.RefreshSessionRuntimeProviders();
            }
            catch (Exception exception)
            {
                ModLog.Warn(
                    "清理联机皮肤运行时失败：" +
                    exception.GetBaseException().Message);
            }
        }

        OnlineSkinCache.EndSession();
    }

    internal static void Tick(double delta)
    {
        var service = _netService;
        if (service == null || !service.IsConnected)
        {
            ContextualSkinControls.RefreshMultiplayerSkinLoadingStatus();
            return;
        }

        _snapshotElapsed += delta;
        var allowOnlineDownloads = UpdateReadyGate(delta);
        OnlineSkinCache.Tick(allowOnlineDownloads);
        ContextualSkinControls.RefreshMultiplayerSkinLoadingStatus();
        UpdateReadyGateCompletion();
        if ((_snapshotStage == 0 && _snapshotElapsed >= 0.75) ||
            (_snapshotStage == 1 && _snapshotElapsed >= 3.0))
        {
            _snapshotStage++;
            BroadcastKnownSelections();
        }

        ulong[] pending;
        bool refreshProviders;
        lock (Sync)
        {
            pending = PendingRefreshes.ToArray();
            refreshProviders = _runtimeProvidersDirty;
        }

        if ((!refreshProviders && pending.Length == 0) ||
            !CharacterAppearanceRuntime.CanApplySelectionImmediately())
        {
            return;
        }

        try
        {
            if (refreshProviders)
            {
                SkinService.RefreshSessionRuntimeProviders();
            }
        }
        catch (Exception exception)
        {
            ModLog.Warn(
                "刷新联机皮肤运行时失败，已停止本次重试：" +
                exception.GetBaseException().Message);
        }
        finally
        {
            lock (Sync)
            {
                _runtimeProvidersDirty = false;
            }
        }

        foreach (var playerId in pending)
        {
            try
            {
                CharacterAppearanceRuntime.RefreshPlayerAppearance(playerId);
            }
            catch (Exception exception)
            {
                ModLog.Warn(
                    $"刷新联机玩家 {playerId} 的外观失败，已停止本次重试：" +
                    exception.GetBaseException().Message);
            }
            lock (Sync)
            {
                PendingRefreshes.Remove(playerId);
            }
        }
    }

    internal static void OnLocalCharacterSelectionChanged(string groupId)
    {
        if (!TryGetLocalCharacter(out _, out var character))
        {
            return;
        }

        var group = ContextualSkinControls.FindGroup(
            character.Id.Entry,
            character.GetType().Name);
        if (group == null || !group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        RememberLocalAdvertisement();
        SendLocalAdvertisement();
        AdvanceReadyGateRevisionAsHost();
    }

    internal static void OnRemoteSkinLoadingPreferenceChanged(bool enabled)
    {
        OnlineSkinCache.OnRemoteSkinLoadingPreferenceChanged(enabled);
        SkinChangerNetMessage[] advertisements;
        lock (Sync)
        {
            advertisements = AdvertisedSelections.Values
                .Where(message => message.PlayerNetId != _netService?.NetId)
                .ToArray();
        }

        foreach (var advertisement in advertisements)
        {
            TryMakeSelectionAvailable(advertisement);
        }
        MarkReadyResolutionActivity();
    }

    internal static void ResetConnectionState()
    {
        DetachFromRun(refreshRuntimeProviders: true);
    }

    internal static bool ShouldAllowBeginRun(StartRunLobby lobby)
    {
        AttachToLobby(lobby);
        if (lobby.NetService.Type != NetGameType.Host ||
            _runReleaseCommitted ||
            !lobby.IsAboutToBeginGame() ||
            !HasCapableLobbyPeer())
        {
            return true;
        }

        BeginReadyGate();
        return false;
    }

    private static bool UpdateReadyGate(double delta)
    {
        var lobby = _lobby;
        if (lobby == null || _runReleaseCommitted)
        {
            return false;
        }

        bool allReady;
        try
        {
            allReady = lobby.IsAboutToBeginGame();
        }
        catch
        {
            allReady = false;
        }

        if (!allReady || !HasCapableLobbyPeer())
        {
            CancelReadyGate();
            return false;
        }

        BeginReadyGate();
        lock (Sync)
        {
            _readyGateElapsed += delta;
            _readyGateQuietElapsed += delta;
            return _readyGateActive && SkinService.ShouldLoadOtherPlayersCustomSkins();
        }
    }

    private static void UpdateReadyGateCompletion()
    {
        bool shouldCompleteLocally;
        lock (Sync)
        {
            shouldCompleteLocally = _readyGateActive &&
                                    !_localReadyResolutionComplete &&
                                    _readyGateQuietElapsed >= ReadyGateQuietSeconds;
        }

        if (shouldCompleteLocally && !OnlineSkinCache.HasPendingWork())
        {
            var service = _netService;
            if (service == null)
            {
                return;
            }

            lock (Sync)
            {
                if (!_readyGateActive || _localReadyResolutionComplete)
                {
                    return;
                }
                _localReadyResolutionComplete = true;
                if (service.Type == NetGameType.Host)
                {
                    ReadyResolutionCompletePeers.Add(service.NetId);
                }
            }

            if (service.Type == NetGameType.Client)
            {
                SendReadyResolutionComplete();
            }
            ModLog.Info("本机已完成联机最终皮肤准备。");
        }

        TryReleaseReadyGateAsHost();
    }

    private static void BeginReadyGate()
    {
        lock (Sync)
        {
            if (_readyGateActive || _runReleaseCommitted)
            {
                return;
            }

            _readyGateActive = true;
            _localReadyResolutionComplete = false;
            _readyGateElapsed = 0;
            _readyGateQuietElapsed = 0;
            ReadyResolutionCompletePeers.Clear();
        }

        BroadcastKnownSelections(includeOnlineMetadata: true);
        AdvanceReadyGateRevisionAsHost();
        ModLog.Info(
            "所有玩家均已准备；已锁定最终角色皮肤，并在开局前处理其他玩家的自定义皮肤。");
    }

    private static void CancelReadyGate()
    {
        bool wasActive;
        lock (Sync)
        {
            wasActive = _readyGateActive;
            _readyGateActive = false;
            _localReadyResolutionComplete = false;
            _readyGateElapsed = 0;
            _readyGateQuietElapsed = 0;
            _readyGateRevision = 0;
            ReadyResolutionCompletePeers.Clear();
        }

        if (wasActive)
        {
            ModLog.Info("有玩家取消准备；已取消本轮最终皮肤准备，返回选角等待状态。");
        }
    }

    private static void ResetReadyGateState()
    {
        lock (Sync)
        {
            _readyGateActive = false;
            _localReadyResolutionComplete = false;
            _runReleaseCommitted = false;
            _readyGateElapsed = 0;
            _readyGateQuietElapsed = 0;
            _readyGateRevision = 0;
            ReadyResolutionCompletePeers.Clear();
        }
    }

    private static void MarkReadyResolutionActivity()
    {
        var service = _netService;
        lock (Sync)
        {
            if (!_readyGateActive)
            {
                return;
            }

            _localReadyResolutionComplete = false;
            _readyGateQuietElapsed = 0;
            if (service != null)
            {
                ReadyResolutionCompletePeers.Remove(service.NetId);
            }
        }
    }

    private static void SendReadyResolutionComplete()
    {
        var service = _netService;
        var hostId = (service as INetClientGameService)?.NetClient?.HostNetId;
        if (service == null || !hostId.HasValue || !IsCapable(hostId.Value))
        {
            return;
        }

        ulong revision;
        lock (Sync)
        {
            revision = _readyGateRevision;
        }
        if (revision == 0)
        {
            return;
        }

        service.SendMessage(new SkinChangerNetMessage
        {
            ProtocolVersion = ProtocolVersion,
            Kind = SkinSyncMessageKind.ReadyResolutionComplete,
            PlayerNetId = service.NetId,
            WorkshopItemId = revision
        });
    }

    private static void AdvanceReadyGateRevisionAsHost()
    {
        var service = _netService;
        if (service?.Type != NetGameType.Host)
        {
            MarkReadyResolutionActivity();
            return;
        }

        ulong revision;
        lock (Sync)
        {
            if (!_readyGateActive)
            {
                return;
            }

            _readyGateRevision++;
            if (_readyGateRevision == 0)
            {
                _readyGateRevision = 1;
            }
            revision = _readyGateRevision;
            _localReadyResolutionComplete = false;
            _readyGateQuietElapsed = 0;
            ReadyResolutionCompletePeers.Clear();
        }

        RelayFromHost(new SkinChangerNetMessage
        {
            ProtocolVersion = ProtocolVersion,
            Kind = SkinSyncMessageKind.ReadyResolutionProbe,
            PlayerNetId = service.NetId,
            WorkshopItemId = revision
        });
        ModLog.Info($"联机皮肤准备已进入第 {revision} 轮；旧轮次完成回执不再有效。");
    }

    private static void TryReleaseReadyGateAsHost()
    {
        var service = _netService;
        var lobby = _lobby;
        if (service?.Type != NetGameType.Host || lobby == null)
        {
            return;
        }

        var capablePeers = GetCapableLobbyPeerIds();
        bool complete;
        bool timedOut;
        lock (Sync)
        {
            complete = _readyGateActive &&
                       _localReadyResolutionComplete &&
                       capablePeers.All(peerId => ReadyResolutionCompletePeers.Contains(peerId));
            timedOut = _readyGateActive &&
                       _readyGateElapsed >= ReadyGateTimeoutSeconds;
        }

        if (!complete && !timedOut)
        {
            return;
        }

        if (timedOut && !complete)
        {
            ModLog.Warn("等待其他玩家完成皮肤准备超时；未完成的皮肤将回退为本地已有皮肤或原皮。");
        }

        lock (Sync)
        {
            _runReleaseCommitted = true;
            _readyGateActive = false;
        }

        try
        {
            AccessTools.Method(typeof(StartRunLobby), "BeginRunForAllPlayersIfAllReady")
                ?.Invoke(lobby, null);
        }
        catch (Exception exception)
        {
            ModLog.Error("完成联机皮肤准备后继续开局失败：" + exception.GetBaseException().Message);
        }
    }

    private static bool HasCapableLobbyPeer() => GetCapableLobbyPeerIds().Length > 0;

    private static ulong[] GetCapableLobbyPeerIds()
    {
        var service = _netService;
        if (service == null)
        {
            return [];
        }

        var lobbyPlayerIds = GetLobbyPlayerIds();
        lock (Sync)
        {
            return lobbyPlayerIds
                .Where(playerId => playerId != service.NetId && CapablePeers.Contains(playerId))
                .ToArray();
        }
    }

    private static ulong[] GetLobbyPlayerIds()
    {
        var lobby = _lobby;
        if (lobby == null ||
            AccessTools.Property(typeof(StartRunLobby), "Players")?.GetValue(lobby)
                is not System.Collections.IEnumerable players)
        {
            return [];
        }

        var result = new List<ulong>();
        foreach (var candidate in players)
        {
            if (candidate != null &&
                AccessTools.Field(candidate.GetType(), "id")?.GetValue(candidate) is ulong id)
            {
                result.Add(id);
            }
        }
        return result.ToArray();
    }

    internal static void MarkPeerCapable(ulong peerId, byte protocolVersion)
    {
        if (protocolVersion != ProtocolVersion)
        {
            ModLog.Warn($"玩家 {peerId} 使用不兼容的 Skin Changer 联机协议 {protocolVersion}。");
            return;
        }

        lock (Sync)
        {
            if (CapablePeers.Add(peerId))
            {
                // A late join or reconnect still needs every existing player's current snapshot.
                _snapshotElapsed = 0;
                _snapshotStage = 0;
                ModLog.Info($"已确认联机玩家 {peerId} 支持 Skin Changer 皮肤同步。");
            }
        }
    }

    internal static bool TryReadCapabilityTrailer(byte[] packetBytes, out byte protocolVersion)
    {
        protocolVersion = 0;
        var trailerLength = CapabilityMagic.Length + 1;
        if (packetBytes.Length < trailerLength)
        {
            return false;
        }

        // Other networking Mods may append their own trailer after message serialization. Search
        // backwards instead of assuming our marker is the final bytes in the transport packet.
        for (var offset = packetBytes.Length - trailerLength; offset >= 0; offset--)
        {
            if (!packetBytes.AsSpan(offset, CapabilityMagic.Length).SequenceEqual(CapabilityMagic))
            {
                continue;
            }

            protocolVersion = packetBytes[offset + CapabilityMagic.Length];
            return true;
        }

        return false;
    }

    internal static void AppendCapabilityTrailer(PacketWriter writer)
    {
        var remainder = writer.BitPosition % 8;
        if (remainder != 0)
        {
            writer.WriteByte(0, 8 - remainder);
        }
        writer.WriteBytes(CapabilityMagic, CapabilityMagic.Length);
        writer.WriteByte(ProtocolVersion);
    }

    private static void HandleMessage(SkinChangerNetMessage message, ulong senderId)
    {
        var service = _netService;
        if (service == null || message.ProtocolVersion != ProtocolVersion)
        {
            return;
        }

        // Receiving our reserved envelope with the current protocol is itself proof that the
        // sender supports Skin Changer. This repairs an asymmetric capability trailer if another
        // networking Mod rewrote only one side of the initial handshake packet.
        var newlyConfirmed = !IsCapable(senderId);
        MarkPeerCapable(senderId, message.ProtocolVersion);

        if (service.Type == NetGameType.Host)
        {
            if (!IsCapable(senderId))
            {
                return;
            }

            if (message.Kind == SkinSyncMessageKind.ReadyResolutionComplete)
            {
                if (message.PlayerNetId != senderId)
                {
                    return;
                }

                lock (Sync)
                {
                    if (!_readyGateActive || message.WorkshopItemId != _readyGateRevision)
                    {
                        return;
                    }
                    ReadyResolutionCompletePeers.Add(senderId);
                }
                ModLog.Info(
                    $"联机玩家 {senderId} 已完成第 {message.WorkshopItemId} 轮最终皮肤准备。");
                return;
            }

            if (message.PlayerNetId != senderId)
            {
                return;
            }
        }
        else
        {
            var hostNetId = (service as INetClientGameService)?.NetClient?.HostNetId;
            if (hostNetId != senderId || !IsCapable(senderId))
            {
                return;
            }
            if (message.Kind == SkinSyncMessageKind.ReadyResolutionProbe)
            {
                if (message.WorkshopItemId == 0)
                {
                    return;
                }

                lock (Sync)
                {
                    _readyGateRevision = message.WorkshopItemId;
                    _localReadyResolutionComplete = false;
                    _readyGateQuietElapsed = 0;
                }
                return;
            }
            if (message.Kind != SkinSyncMessageKind.CharacterSelection)
            {
                return;
            }

            if (newlyConfirmed)
            {
                RememberLocalAdvertisement();
                SendLocalAdvertisement();
            }
        }

        if (message.Kind != SkinSyncMessageKind.CharacterSelection ||
            !ValidateText(message.CharacterId) ||
            !ValidateText(message.GroupId) ||
            !ValidateText(message.OptionId) ||
            !ValidateOptionalText(message.ProviderId) ||
            !ValidateOptionalText(message.SafeResourceFingerprint))
        {
            return;
        }

        RememberAdvertisement(message);
        OnlineSkinCache.DiscardPendingSelectionsForPlayer(message.PlayerNetId);
        if (service.Type == NetGameType.Host)
        {
            RelayFromHost(message, exceptPeerId: senderId);
        }

        TryMakeSelectionAvailable(message);
        if (service.Type == NetGameType.Host && IsReadyGateActive())
        {
            AdvanceReadyGateRevisionAsHost();
        }
        else
        {
            MarkReadyResolutionActivity();
        }
    }

    private static bool ValidateText(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 512;

    private static bool ValidateOptionalText(string? value) => value == null || value.Length <= 512;

    private static bool IsCapable(ulong peerId)
    {
        lock (Sync)
        {
            return CapablePeers.Contains(peerId);
        }
    }

    private static void TryMakeSelectionAvailable(SkinChangerNetMessage message)
    {
        // The host periodically relays every known selection to late joiners. A client can
        // therefore receive its own older snapshot back from the host; never let session state
        // override the local player's current persistent choice.
        if (_netService?.NetId == message.PlayerNetId)
        {
            lock (Sync)
            {
                AvailableSelections.Remove(message.PlayerNetId);
                PendingRefreshes.Remove(message.PlayerNetId);
            }
            return;
        }

        if (!PlayerMatchesCharacterSelection(message))
        {
            return;
        }

        var allowRemoteSkin = SkinService.ShouldLoadOtherPlayersCustomSkins();
        var effectiveOptionId = allowRemoteSkin
            ? message.OptionId
            : SkinCatalog.BaseOptionId;
        var selectionAvailable = SkinService.TryBuildSessionCharacterSelection(
            message.GroupId,
            effectiveOptionId,
            out var selectionOverrides);
        if (!selectionAvailable && allowRemoteSkin &&
            OnlineSkinCache.TryGetCachedOption(message, out var cachedOptionId))
        {
            effectiveOptionId = cachedOptionId;
            selectionAvailable = SkinService.TryBuildSessionCharacterSelection(
                message.GroupId,
                effectiveOptionId,
                out selectionOverrides);
        }

        if (!selectionAvailable && allowRemoteSkin)
        {
            var queued = OnlineSkinCache.QueueMissingSelection(message);
            if (queued)
            {
                MarkReadyResolutionActivity();
            }
            else if (message.OptionId != SkinCatalog.BaseOptionId &&
                     !OnlineSkinCache.HasDownloadMetadata(message) &&
                     IsReadyGateActive())
            {
                OnlineSkinCache.ReportMissingMetadata(message);
                ModLog.Info(
                    $"联机玩家 {message.PlayerNetId} 的皮肤 {message.OptionId} " +
                    "没有携带可下载资源信息，暂时显示原皮。");
            }
            // Cover a cache registration racing this advertisement before falling back.
            if (OnlineSkinCache.TryGetCachedOption(message, out cachedOptionId))
            {
                effectiveOptionId = cachedOptionId;
                selectionAvailable = SkinService.TryBuildSessionCharacterSelection(
                    message.GroupId,
                    effectiveOptionId,
                    out selectionOverrides);
            }
        }

        if (!selectionAvailable)
        {
            // The sender owns a skin we do not have. Keep a per-player base selection instead of
            // leaking our own skin for the same character onto that remote player.
            if (!SkinService.TryBuildSessionCharacterSelection(
                    message.GroupId,
                    SkinCatalog.BaseOptionId,
                    out selectionOverrides))
            {
                lock (Sync)
                {
                    AvailableSelections.Remove(message.PlayerNetId);
                    PendingRefreshes.Add(message.PlayerNetId);
                    _runtimeProvidersDirty = true;
                }
                return;
            }
            effectiveOptionId = SkinCatalog.BaseOptionId;
        }

        lock (Sync)
        {
            AvailableSelections[message.PlayerNetId] = new SessionCharacterSelection(
                message.CharacterId,
                message.GroupId,
                effectiveOptionId,
                selectionOverrides);
            PendingRefreshes.Add(message.PlayerNetId);
            _runtimeProvidersDirty = true;
        }
    }

    private static bool IsReadyGateActive()
    {
        lock (Sync)
        {
            return _readyGateActive;
        }
    }

    private static void RememberLocalAdvertisement(bool includeOnlineMetadata = false)
    {
        if (!TryGetLocalCharacter(out var playerNetId, out var character))
        {
            return;
        }

        var group = ContextualSkinControls.FindGroup(
            character.Id.Entry,
            character.GetType().Name);
        if (group == null)
        {
            return;
        }

        var message = new SkinChangerNetMessage
        {
            ProtocolVersion = ProtocolVersion,
            Kind = SkinSyncMessageKind.CharacterSelection,
            PlayerNetId = playerNetId,
            CharacterId = character.Id.Entry,
            GroupId = group.Id,
            OptionId = SkinService.Config.GetSelection(group.Id)
        };
        if (includeOnlineMetadata && OnlineSkinCache.TryDescribeLocalSelection(
                message.GroupId,
                message.OptionId,
                out var source))
        {
            message.ProviderId = source.ProviderId;
            message.WorkshopItemId = source.WorkshopItemId;
            message.SafeResourceFingerprint = source.SafeResourceFingerprint;
        }
        RememberAdvertisement(message);
    }

    internal static void RetryCachedSelection(
        SkinChangerNetMessage message,
        string sessionOptionId)
    {
        lock (Sync)
        {
            if (!AdvertisedSelections.TryGetValue(message.PlayerNetId, out var current) ||
                !current.CharacterId.Equals(message.CharacterId, StringComparison.OrdinalIgnoreCase) ||
                !current.GroupId.Equals(message.GroupId, StringComparison.OrdinalIgnoreCase) ||
                !current.OptionId.Equals(message.OptionId, StringComparison.OrdinalIgnoreCase) ||
                current.WorkshopItemId != message.WorkshopItemId ||
                !current.SafeResourceFingerprint.Equals(
                    message.SafeResourceFingerprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        message.OptionId = sessionOptionId;
        TryMakeSelectionAvailable(message);
    }

    internal static void OnLocalOnlineMetadataReady(string groupId, string optionId)
    {
        if (!TryGetLocalCharacter(out _, out _) ||
            !SkinService.Config.GetSelection(groupId).Equals(
                optionId,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        RememberLocalAdvertisement(includeOnlineMetadata: true);
        SendLocalAdvertisement();
        AdvanceReadyGateRevisionAsHost();
    }

    private static bool TryGetLocalCharacter(
        out ulong playerNetId,
        out CharacterModel character)
    {
        var service = _netService;
        if (service != null && TryGetLobbyCharacter(service.NetId, out character))
        {
            playerNetId = service.NetId;
            return true;
        }

        var player = CharacterAppearanceRuntime.GetLocalPlayer();
        if (player != null)
        {
            playerNetId = player.NetId;
            character = player.Character;
            return true;
        }

        playerNetId = 0;
        character = null!;
        return false;
    }

    private static bool PlayerMatchesCharacterSelection(SkinChangerNetMessage message)
    {
        if (TryGetLobbyCharacter(message.PlayerNetId, out var character))
        {
            var group = ContextualSkinControls.FindGroup(
                character.Id.Entry,
                character.GetType().Name);
            return character.Id.Entry.Equals(
                       message.CharacterId,
                       StringComparison.OrdinalIgnoreCase) &&
                   group?.Id.Equals(
                       message.GroupId,
                       StringComparison.OrdinalIgnoreCase) == true;
        }

        return CharacterAppearanceRuntime.PlayerMatchesCharacterSelection(
            message.PlayerNetId,
            message.CharacterId,
            message.GroupId);
    }

    private static bool TryGetLobbyCharacter(ulong playerNetId, out CharacterModel character)
    {
        character = null!;
        var lobby = _lobby;
        if (lobby == null)
        {
            return false;
        }

        try
        {
            // 0.107.1 exposes List<LobbyPlayer>, while 0.111.0 exposes
            // List<StartRunLobbyPlayer>. Reflection keeps the same AnyCPU DLL compatible with
            // both return signatures.
            if (AccessTools.Property(typeof(StartRunLobby), "Players")?.GetValue(lobby)
                    is not System.Collections.IEnumerable players)
            {
                return false;
            }

            foreach (var candidate in players)
            {
                if (candidate == null)
                {
                    continue;
                }

                var candidateType = candidate.GetType();
                if (AccessTools.Field(candidateType, "id")?.GetValue(candidate) is ulong id &&
                    id == playerNetId &&
                    AccessTools.Field(candidateType, "character")?.GetValue(candidate)
                        is CharacterModel candidateCharacter)
                {
                    character = candidateCharacter;
                    return true;
                }
            }
        }
        catch (Exception exception)
        {
            ModLog.Warn("读取联机选角玩家失败：" + exception.GetBaseException().Message);
        }

        return false;
    }

    private static void RememberAdvertisement(SkinChangerNetMessage message)
    {
        lock (Sync)
        {
            AdvertisedSelections[message.PlayerNetId] = message;
        }
    }

    private static void SendLocalAdvertisement()
    {
        var service = _netService;
        if (service == null)
        {
            return;
        }

        SkinChangerNetMessage message;
        lock (Sync)
        {
            if (!AdvertisedSelections.TryGetValue(service.NetId, out message))
            {
                return;
            }
        }

        if (service is INetHostGameService)
        {
            RelayFromHost(message);
        }
        else
        {
            var hostId = (service as INetClientGameService)?.NetClient?.HostNetId;
            if (hostId.HasValue && IsCapable(hostId.Value))
            {
                service.SendMessage(message);
            }
        }
    }

    private static void BroadcastKnownSelections(bool includeOnlineMetadata = false)
    {
        RememberLocalAdvertisement(includeOnlineMetadata);
        var service = _netService;
        if (service is INetHostGameService)
        {
            SkinChangerNetMessage[] messages;
            lock (Sync)
            {
                messages = AdvertisedSelections.Values.ToArray();
            }
            foreach (var message in messages)
            {
                RelayFromHost(message);
            }
        }
        else
        {
            SendLocalAdvertisement();
        }
    }

    private static void RelayFromHost(
        SkinChangerNetMessage message,
        ulong? exceptPeerId = null)
    {
        if (_netService is not INetHostGameService host)
        {
            return;
        }

        ulong[] peers;
        lock (Sync)
        {
            peers = CapablePeers.ToArray();
        }
        foreach (var peerId in peers)
        {
            if (peerId != exceptPeerId)
            {
                host.SendMessage(message, peerId);
            }
        }
    }

    private static void OnClientDisconnected(ulong peerId, NetErrorInfo _)
    {
        lock (Sync)
        {
            CapablePeers.Remove(peerId);
            AdvertisedSelections.Remove(peerId);
            if (AvailableSelections.Remove(peerId))
            {
                _runtimeProvidersDirty = true;
            }
            PendingRefreshes.Remove(peerId);
        }
    }

    private static void OnDisconnected(NetErrorInfo _) =>
        DetachFromRun(refreshRuntimeProviders: true);

    private sealed class SelectionScope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            var scopes = _selectionScopes;
            if (scopes is { Count: > 0 })
            {
                scopes.Pop();
            }
        }
    }
}

internal partial class MultiplayerSkinSyncNode : Node
{
    public override void _Ready() => MultiplayerSkinSync.AttachToRun();

    public override void _Process(double delta) => MultiplayerSkinSync.Tick(delta);

    public override void _ExitTree() => MultiplayerSkinSync.DetachFromRun(
        clearCapabilities: false,
        refreshRuntimeProviders: true);
}

[HarmonyPatch(typeof(NCharacterSelectScreen), "AfterInitialized")]
internal static class MultiplayerSkinLobbyAttachPatch
{
    private static void Postfix(NCharacterSelectScreen __instance) =>
        MultiplayerSkinSync.AttachToLobby(__instance.Lobby);
}

[HarmonyPatch(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen._Process))]
internal static class MultiplayerSkinLobbyTickPatch
{
    private static void Postfix(double delta) => MultiplayerSkinSync.Tick(delta);
}

[HarmonyPatch(typeof(StartRunLobby), "BeginRunForAllPlayersIfAllReady")]
internal static class MultiplayerSkinReadyGatePatch
{
    private static bool Prefix(StartRunLobby __instance) =>
        MultiplayerSkinSync.ShouldAllowBeginRun(__instance);
}

[HarmonyPatch(typeof(MessageTypes), nameof(MessageTypes.ToId))]
internal static class SkinChangerMessageIdWritePatch
{
    private static bool Prefix(INetMessage message, ref int __result)
    {
        if (message is not SkinChangerNetMessage)
        {
            return true;
        }

        __result = MultiplayerSkinSync.ReservedMessageId;
        return false;
    }
}

[HarmonyPatch(typeof(MessageTypes), nameof(MessageTypes.TryGetMessageType))]
internal static class SkinChangerMessageIdReadPatch
{
    private static bool Prefix(int id, ref Type? type, ref bool __result)
    {
        if (id != MultiplayerSkinSync.ReservedMessageId)
        {
            return true;
        }

        type = typeof(SkinChangerNetMessage);
        __result = true;
        return false;
    }
}

[HarmonyPatch]
internal static class SkinChangerCapabilityTrailerPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(InitialGameInfoMessage), nameof(InitialGameInfoMessage.Serialize));
        yield return AccessTools.Method(typeof(ClientLobbyJoinRequestMessage), nameof(ClientLobbyJoinRequestMessage.Serialize));
        yield return AccessTools.Method(typeof(ClientLoadJoinRequestMessage), nameof(ClientLoadJoinRequestMessage.Serialize));
        yield return AccessTools.Method(typeof(ClientRejoinRequestMessage), nameof(ClientRejoinRequestMessage.Serialize));
    }

    private static void Postfix(PacketWriter writer) =>
        MultiplayerSkinSync.AppendCapabilityTrailer(writer);
}

[HarmonyPatch]
internal static class SkinChangerCapabilityReceivePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(NetHostGameService), nameof(NetHostGameService.OnPacketReceived));
        yield return AccessTools.Method(typeof(NetClientGameService), nameof(NetClientGameService.OnPacketReceived));
    }

    private static void Prefix(ulong senderId, byte[] packetBytes)
    {
        if (MultiplayerSkinSync.TryReadCapabilityTrailer(packetBytes, out var protocolVersion))
        {
            MultiplayerSkinSync.MarkPeerCapable(senderId, protocolVersion);
        }
    }
}

[HarmonyPatch(typeof(NetClientGameService), nameof(NetClientGameService.Initialize))]
internal static class SkinChangerClientConnectionResetPatch
{
    private static void Prefix() => MultiplayerSkinSync.ResetConnectionState();
}

[HarmonyPatch]
internal static class SkinChangerHostConnectionResetPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(NetHostGameService), nameof(NetHostGameService.StartSteamHost));
        yield return AccessTools.Method(typeof(NetHostGameService), nameof(NetHostGameService.StartENetHost));
    }

    private static void Prefix() => MultiplayerSkinSync.ResetConnectionState();
}

[HarmonyPatch(typeof(Creature), nameof(Creature.CreateVisuals))]
internal static class MultiplayerCreatureVisualScopePatch
{
    private static void Prefix(Creature __instance, out IDisposable? __state) =>
        __state = MultiplayerSkinSync.BeginCreatureSelectionScope(__instance);

    private static Exception? Finalizer(Exception? __exception, IDisposable? __state)
    {
        __state?.Dispose();
        return __exception;
    }
}
