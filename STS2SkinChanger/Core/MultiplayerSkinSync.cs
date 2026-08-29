using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Runs;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Ui;

namespace STS2SkinChanger.Core;

internal enum SkinSyncMessageKind : byte
{
    CharacterSelection = 1
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
    }

    public void Deserialize(PacketReader reader)
    {
        ProtocolVersion = reader.ReadByte();
        Kind = (SkinSyncMessageKind)reader.ReadByte();
        PlayerNetId = reader.ReadULong();
        CharacterId = reader.ReadString();
        GroupId = reader.ReadString();
        OptionId = reader.ReadString();
    }
}

internal sealed record SessionCharacterSelection(
    string CharacterId,
    string GroupId,
    string OptionId,
    IReadOnlyDictionary<string, string> SelectionOverrides);

internal static class MultiplayerSkinSync
{
    internal const byte ProtocolVersion = 1;
    internal const int ReservedMessageId = 254;

    private static readonly byte[] CapabilityMagic =
        [0x47, 0x53, 0x43, 0x41, 0x50, 0x30, 0x31, 0x21]; // GSCAP01!
    private static readonly HashSet<ulong> CapablePeers = [];
    private static readonly Dictionary<ulong, SkinChangerNetMessage> AdvertisedSelections = [];
    private static readonly Dictionary<ulong, SessionCharacterSelection> AvailableSelections = [];
    private static readonly HashSet<ulong> PendingRefreshes = [];
    private static readonly object Sync = new();

    [ThreadStatic]
    private static Stack<IReadOnlyDictionary<string, string>>? _selectionScopes;

    private static INetGameService? _netService;
    private static MessageHandlerDelegate<SkinChangerNetMessage>? _messageHandler;
    private static double _snapshotElapsed;
    private static int _snapshotStage;
    private static bool _runtimeProvidersDirty;

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
        if (!service.Type.IsMultiplayer() || ReferenceEquals(service, _netService))
        {
            return;
        }

        DetachFromRun(clearCapabilities: false);
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
        RememberLocalAdvertisement();
        ModLog.Info("多人角色皮肤同步已启用；仅向确认安装 Skin Changer 的玩家发送选择。");
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
        _messageHandler = null;
        bool hadRemoteSelections;
        lock (Sync)
        {
            hadRemoteSelections = AvailableSelections.Count > 0;
            AdvertisedSelections.Clear();
            AvailableSelections.Clear();
            PendingRefreshes.Clear();
            _runtimeProvidersDirty = false;
            if (clearCapabilities)
            {
                CapablePeers.Clear();
            }
        }

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
    }

    internal static void Tick(double delta)
    {
        var service = _netService;
        if (service == null || !service.IsConnected)
        {
            return;
        }

        _snapshotElapsed += delta;
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
        var player = CharacterAppearanceRuntime.GetLocalPlayer();
        if (player == null)
        {
            return;
        }

        var group = ContextualSkinControls.FindGroup(
            player.Character.Id.Entry,
            player.Character.GetType().Name);
        if (group == null || !group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        RememberLocalAdvertisement();
        SendLocalAdvertisement();
    }

    internal static void ResetConnectionState()
    {
        DetachFromRun(refreshRuntimeProviders: true);
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

        var offset = packetBytes.Length - trailerLength;
        if (!packetBytes.AsSpan(offset, CapabilityMagic.Length).SequenceEqual(CapabilityMagic))
        {
            return false;
        }

        protocolVersion = packetBytes[^1];
        return true;
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
        if (service == null || message.ProtocolVersion != ProtocolVersion ||
            message.Kind != SkinSyncMessageKind.CharacterSelection ||
            !ValidateText(message.CharacterId) ||
            !ValidateText(message.GroupId) ||
            !ValidateText(message.OptionId))
        {
            return;
        }

        if (service.Type == NetGameType.Host)
        {
            if (!IsCapable(senderId) || message.PlayerNetId != senderId)
            {
                return;
            }

            RememberAdvertisement(message);
            RelayFromHost(message, exceptPeerId: senderId);
        }
        else
        {
            var hostNetId = (service as INetClientGameService)?.NetClient?.HostNetId;
            if (hostNetId != senderId || !IsCapable(senderId))
            {
                return;
            }

            RememberAdvertisement(message);
        }

        TryMakeSelectionAvailable(message);
    }

    private static bool ValidateText(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 512;

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

        if (!CharacterAppearanceRuntime.PlayerMatchesCharacterSelection(
                message.PlayerNetId,
                message.CharacterId,
                message.GroupId))
        {
            return;
        }

        if (!SkinService.TryBuildSessionCharacterSelection(
                message.GroupId,
                message.OptionId,
                out var selectionOverrides))
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
        }

        lock (Sync)
        {
            AvailableSelections[message.PlayerNetId] = new SessionCharacterSelection(
                message.CharacterId,
                message.GroupId,
                message.OptionId,
                selectionOverrides);
            PendingRefreshes.Add(message.PlayerNetId);
            _runtimeProvidersDirty = true;
        }
    }

    private static void RememberLocalAdvertisement()
    {
        var player = CharacterAppearanceRuntime.GetLocalPlayer();
        if (player == null)
        {
            return;
        }

        var group = ContextualSkinControls.FindGroup(
            player.Character.Id.Entry,
            player.Character.GetType().Name);
        if (group == null)
        {
            return;
        }

        var message = new SkinChangerNetMessage
        {
            ProtocolVersion = ProtocolVersion,
            Kind = SkinSyncMessageKind.CharacterSelection,
            PlayerNetId = player.NetId,
            CharacterId = player.Character.Id.Entry,
            GroupId = group.Id,
            OptionId = SkinService.Config.GetSelection(group.Id)
        };
        RememberAdvertisement(message);
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

    private static void BroadcastKnownSelections()
    {
        RememberLocalAdvertisement();
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
