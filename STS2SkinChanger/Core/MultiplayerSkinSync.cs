using System.Reflection;
using System.Text.Json;
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
using MegaCrit.Sts2.Core.Nodes;
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
    public string SafeResourceManifest;
    public string SafeResourceBindings;
    public string TransformManifest;
    public string OnlineFailure;

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
        writer.WriteString(SafeResourceManifest ?? string.Empty);
        writer.WriteString(SafeResourceBindings ?? string.Empty);
        writer.WriteString(TransformManifest ?? string.Empty);
        writer.WriteString(OnlineFailure ?? string.Empty);
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
        SafeResourceManifest = reader.ReadString();
        SafeResourceBindings = reader.ReadString();
        TransformManifest = reader.ReadString();
        OnlineFailure = reader.ReadString();
    }
}

internal sealed record SessionCharacterSelection(
    string CharacterId,
    string GroupId,
    string OptionId,
    IReadOnlyDictionary<string, string> SelectionOverrides,
    IReadOnlyDictionary<string, CharacterCombatTransform> Transforms,
    bool OwnerAppearanceLoaded);

internal static class MultiplayerSkinSync
{
    internal const byte ProtocolVersion = 7;
    internal const int ReservedMessageId = 254;
    private const double ReadyGateQuietSeconds = 0.75;
    private const double ReadyGateTimeoutSeconds = 180.0;

    private static readonly byte[] CapabilityMagic =
        [0x47, 0x53, 0x43, 0x41, 0x50, 0x30, 0x37, 0x21]; // GSCAP07!
    private static readonly HashSet<ulong> CapablePeers = [];
    private static readonly Dictionary<ulong, SkinChangerNetMessage> AdvertisedSelections = [];
    private static readonly Dictionary<ulong, SessionCharacterSelection> AvailableSelections = [];
    private static readonly Dictionary<ulong, string> LocalFallbackSelections = [];
    private static readonly Dictionary<(ulong PlayerId, string TransformKey), CharacterCombatTransform>
        LocalFallbackTransforms = [];
    private static readonly HashSet<ulong> PendingRefreshes = [];
    private static readonly HashSet<ulong> PendingTransformRefreshes = [];
    private static readonly HashSet<ulong> PendingIconRefreshes = [];
    private static readonly Dictionary<ulong, string> LastReceivedTransformSignatures = [];
    private static readonly Dictionary<ulong, string> LastAppliedTransformSignatures = [];
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
    private static bool _localTransformAdvertisementDirty;
    private static double _localTransformBroadcastCooldown;
    private static string? _lastSentTransformSignature;
    private static bool _inRun;
    private static bool _hasLobbySession;
    // The combat scene exits before the next StartRunLobby is attached, and both scenes reuse
    // the same Steam net service.  Keep this hand-off marker separate from _inRun so the lobby
    // attach can still invalidate the previous round's temporary providers and advertisements.
    private static bool _needsLobbyRoundReset;

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

    internal static bool CanEditSkinForCreature(Creature creature)
    {
        var owner = creature.Player ?? creature.PetOwner;
        var service = _netService;
        if (owner == null || service == null || !service.Type.IsMultiplayer())
        {
            return true;
        }
        if (owner.NetId == service.NetId)
        {
            return false;
        }

        lock (Sync)
        {
            return !AvailableSelections.TryGetValue(owner.NetId, out var selection) ||
                   !selection.OwnerAppearanceLoaded;
        }
    }

    internal static bool CanEditLocalPlayerSkinInRun() =>
        _netService == null || !_netService.Type.IsMultiplayer();

    internal static void RequestIconRefresh(ulong playerNetId)
    {
        lock (Sync)
        {
            PendingIconRefreshes.Add(playerNetId);
        }
    }

    internal static bool CanEditTransformForCreature(Creature creature)
    {
        var owner = creature.Player ?? creature.PetOwner;
        var service = _netService;
        if (owner == null || service == null || !service.Type.IsMultiplayer() ||
            owner.NetId == service.NetId)
        {
            return true;
        }

        lock (Sync)
        {
            return !AvailableSelections.TryGetValue(owner.NetId, out var selection) ||
                   !selection.OwnerAppearanceLoaded;
        }
    }

    internal static bool UsesLocalFallbackControls(Creature creature)
    {
        var owner = creature.Player ?? creature.PetOwner;
        var service = _netService;
        return owner != null &&
               service != null &&
               service.Type.IsMultiplayer() &&
               owner.NetId != service.NetId &&
               CanEditTransformForCreature(creature);
    }

    internal static bool TryGetSyncedTransform(
        Creature creature,
        string transformKey,
        out CharacterCombatTransform transform)
    {
        var owner = creature.Player ?? creature.PetOwner;
        var service = _netService;
        if (owner == null || service == null || !service.Type.IsMultiplayer() ||
            owner.NetId == service.NetId)
        {
            transform = null!;
            return false;
        }

        lock (Sync)
        {
            if (AvailableSelections.TryGetValue(owner.NetId, out var selection) &&
                selection.OwnerAppearanceLoaded)
            {
                transform = selection.Transforms.GetValueOrDefault(transformKey) ??
                            new CharacterCombatTransform();
                return true;
            }
            if (LocalFallbackTransforms.TryGetValue((owner.NetId, transformKey), out transform!))
            {
                return true;
            }
            // A remote skin may still be downloading (or may be unavailable on this machine),
            // but its owner can already have broadcast model/UI parameters.  Keep those values
            // instead of silently dropping them just because the visual resource is not ready;
            // they apply to the current base/fallback model and are replaced automatically once
            // the safe skin package becomes available.
            if (AvailableSelections.TryGetValue(owner.NetId, out selection) &&
                selection.Transforms.TryGetValue(transformKey, out transform!))
            {
                return true;
            }
        }

        transform = null!;
        return false;
    }

    internal static CharacterCombatTransform SetLocalFallbackTransform(
        Creature creature,
        string transformKey,
        CharacterCombatTransform value)
    {
        var owner = creature.Player ?? creature.PetOwner;
        if (owner == null || !CanEditTransformForCreature(creature))
        {
            return TryGetSyncedTransform(creature, transformKey, out var current)
                ? current
                : new CharacterCombatTransform();
        }

        var service = _netService;
        if (service == null || !service.Type.IsMultiplayer() || owner.NetId == service.NetId)
        {
            return SkinService.NormalizeCharacterCombatTransform(value);
        }

        var normalized = SkinService.NormalizeCharacterCombatTransform(value);
        lock (Sync)
        {
            LocalFallbackTransforms[(owner.NetId, transformKey)] = normalized;
        }
        return normalized;
    }

    internal static bool TrySetLocalFallbackSkin(
        Creature creature,
        string groupId,
        string optionId,
        out string? error)
    {
        error = null;
        var owner = creature.Player ?? creature.PetOwner;
        var service = _netService;
        if (owner == null || service == null || owner.NetId == service.NetId ||
            !service.Type.IsMultiplayer() || !CanEditSkinForCreature(creature))
        {
            error = "当前联机外观由角色所属玩家控制。";
            return false;
        }
        if (!SkinService.TryBuildSessionCharacterSelection(
                groupId,
                optionId,
                out var selections))
        {
            error = "找不到所选角色外观。";
            return false;
        }

        lock (Sync)
        {
            LocalFallbackSelections[owner.NetId] = optionId;
            foreach (var key in LocalFallbackTransforms.Keys
                         .Where(key => key.PlayerId == owner.NetId)
                         .ToArray())
            {
                LocalFallbackTransforms.Remove(key);
            }
            AvailableSelections[owner.NetId] = new SessionCharacterSelection(
                owner.Character.Id.Entry,
                groupId,
                optionId,
                selections,
                new Dictionary<string, CharacterCombatTransform>(
                    StringComparer.OrdinalIgnoreCase),
                OwnerAppearanceLoaded: false);
            PendingRefreshes.Add(owner.NetId);
            PendingTransformRefreshes.Remove(owner.NetId);
            PendingIconRefreshes.Add(owner.NetId);
            _runtimeProvidersDirty = true;
        }
        return true;
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
                ContextualSkinControls.MatchesGroupIdentity(
                    selection.GroupId,
                    player.Character.Id.Entry,
                    player.Character.GetType().Name))
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

    /// <summary>
    /// Temporarily selects a remote player's complete visual map while the game builds a UI
    /// object for that player (for example the multiplayer health-bar avatar). CharacterModel's
    /// icon getters do not receive a Player argument, so the caller must provide this context.
    /// </summary>
    internal static IDisposable? BeginPlayerSelectionScope(ulong playerNetId)
    {
        IReadOnlyDictionary<string, string>? selections = null;
        lock (Sync)
        {
            if (AvailableSelections.TryGetValue(playerNetId, out var selection))
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
        _needsLobbyRoundReset = false;
        if (!service.Type.IsMultiplayer())
        {
            return;
        }

        AttachToService(service, "对局");
        _inRun = true;
        RememberLocalAdvertisement(includeOnlineMetadata: true);
        // Lobby avatar nodes are intentionally discarded when the run scene is created. Queue
        // every already-known remote player once more so the first combat HUD construction uses
        // that player's selected icon instead of the lobby/base texture.
        lock (Sync)
        {
            foreach (var playerId in AvailableSelections.Keys)
            {
                PendingIconRefreshes.Add(playerId);
            }
        }
    }

    internal static void AttachToLobby(StartRunLobby lobby)
    {
        var service = lobby.NetService;
        if (!service.Type.IsMultiplayer())
        {
            return;
        }

        var changedLobby = !ReferenceEquals(_lobby, lobby);
        // The game keeps the same Steam net service while returning from a run to a new
        // character-select lobby.  AttachToService therefore does not run again, so the old
        // per-run provider/cache state would otherwise survive and make the next "ready" phase
        // appear to finish instantly.  Start a fresh online-cache generation at this boundary.
        // Some game versions tear down the multiplayer runtime node before it can set the
        // hand-off marker; an actual new lobby is still an unambiguous round boundary once a
        // previous lobby has existed.
        var resetRound = _needsLobbyRoundReset || (changedLobby && _hasLobbySession);
        var serviceAlreadyAttached = ReferenceEquals(service, _netService);
        if (resetRound)
        {
            ResetRoundStateForLobby();
            // AttachToService intentionally returns when Steam reuses the same connection.  In
            // that case start the replacement cache generation here instead of accidentally
            // leaving the new lobby with an ended session (or the previous round's instant-hit
            // providers).
            if (serviceAlreadyAttached)
            {
                OnlineSkinCache.BeginSession();
            }
        }
        _needsLobbyRoundReset = false;

        AttachToService(service, "联机选角");
        _lobby = lobby;
        _hasLobbySession = true;
        _inRun = false;
        if (changedLobby)
        {
            ResetReadyGateState();
        }
        RememberLocalAdvertisement();
    }

    private static void ResetRoundStateForLobby()
    {
        bool hadRemoteSelections;
        lock (Sync)
        {
            hadRemoteSelections = AvailableSelections.Count > 0;
            AdvertisedSelections.Clear();
            AvailableSelections.Clear();
            LocalFallbackSelections.Clear();
            LocalFallbackTransforms.Clear();
            PendingRefreshes.Clear();
            PendingTransformRefreshes.Clear();
            PendingIconRefreshes.Clear();
            LastReceivedTransformSignatures.Clear();
            LastAppliedTransformSignatures.Clear();
            ReadyResolutionCompletePeers.Clear();
            _runtimeProvidersDirty = false;
            _localTransformAdvertisementDirty = false;
            _localTransformBroadcastCooldown = 0;
            _lastSentTransformSignature = null;
        }

        ResetReadyGateState();
        // EndSession removes every temporary provider and deletes this round's private package.
        // AttachToService creates the replacement generation immediately afterward; keeping the
        // creation there avoids opening two generations during the lobby scene hand-off.
        OnlineSkinCache.EndSession();
        if (hadRemoteSelections)
        {
            try
            {
                SkinService.RefreshSessionRuntimeProviders();
            }
            catch (Exception exception)
            {
                ModLog.Warn("清理上一局联机皮肤运行时失败：" + exception.GetBaseException().Message);
            }
        }

        _snapshotElapsed = 0;
        _snapshotStage = 0;
        ModLog.Info("已进入新的联机选角回合；上一回合的临时皮肤和安全缓存已清理。");
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
        bool refreshRuntimeProviders = false,
        bool clearOnlineCache = false)
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

        // Leaving a lobby or tearing down a scene is not the end of the run.  Keep the online
        // package and its providers alive so re-entering the same room does not immediately
        // rebuild/download everything.  The next lobby is marked as a new round and will clear
        // the old generation before it starts.  Explicit run cleanup is the only other point
        // that removes the temporary package.
        if (service != null && !clearOnlineCache)
        {
            _needsLobbyRoundReset = true;
        }
        else if (clearOnlineCache)
        {
            _needsLobbyRoundReset = false;
        }
        _netService = null;
        _lobby = null;
        _messageHandler = null;
        _inRun = false;
        bool hadRemoteSelections;
        lock (Sync)
        {
            hadRemoteSelections = AvailableSelections.Count > 0;
            AdvertisedSelections.Clear();
            AvailableSelections.Clear();
            LocalFallbackSelections.Clear();
            LocalFallbackTransforms.Clear();
            PendingRefreshes.Clear();
            PendingTransformRefreshes.Clear();
            PendingIconRefreshes.Clear();
            LastReceivedTransformSignatures.Clear();
            LastAppliedTransformSignatures.Clear();
            ReadyResolutionCompletePeers.Clear();
            _runtimeProvidersDirty = false;
            _localTransformAdvertisementDirty = false;
            _localTransformBroadcastCooldown = 0;
            _lastSentTransformSignature = null;
            if (clearCapabilities)
            {
                _hasLobbySession = false;
                CapablePeers.Clear();
            }
        }

        ResetReadyGateState();

        if (clearOnlineCache)
        {
            // Providers must be removed from the catalog before rebuilding the canonical
            // overlay.  Do this only at an explicit run/new-game boundary; a room disconnect
            // intentionally keeps the package available for the next connection.
            OnlineSkinCache.EndSession();

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

    }

    internal static void Tick(double delta)
    {
        var service = _netService;
        if (service == null || !service.IsConnected)
        {
            ContextualSkinControls.RefreshMultiplayerSkinLoadingStatus();
            return;
        }

        FlushLocalTransformAdvertisement(delta);

        _snapshotElapsed += delta;
        var allowOnlineDownloads = UpdateReadyGate(delta);
        OnlineSkinCache.Tick(allowOnlineDownloads);
        MultiplayerSkinFailureDialog.TryShow();
        ContextualSkinControls.RefreshMultiplayerSkinLoadingStatus();
        UpdateReadyGateCompletion();
        if ((_snapshotStage == 0 && _snapshotElapsed >= 0.75) ||
            (_snapshotStage == 1 && _snapshotElapsed >= 3.0))
        {
            _snapshotStage++;
            BroadcastKnownSelections();
        }

        ulong[] pending;
        ulong[] pendingTransforms;
        ulong[] pendingIcons;
        bool refreshProviders;
        lock (Sync)
        {
            pending = PendingRefreshes.ToArray();
            pendingTransforms = PendingTransformRefreshes
                .Where(playerId => !PendingRefreshes.Contains(playerId))
                .ToArray();
            pendingIcons = PendingIconRefreshes.ToArray();
            PendingIconRefreshes.Clear();
            refreshProviders = _runtimeProvidersDirty;
        }

        // A newly registered online provider must be mounted before an avatar getter can resolve
        // its icon.  Previously icon refresh ran first, so the node was marked handled while the
        // old/base resource was still mounted.  Keep the icon request pending until this mount is
        // allowed, then refresh it below.
        if (refreshProviders && CharacterAppearanceRuntime.CanApplySelectionImmediately())
        {
            try
            {
                SkinService.RefreshSessionRuntimeProviders();
            }
            catch (Exception exception)
            {
                ModLog.Warn(
                    "刷新联机皮肤运行时失败，头像刷新将重试：" +
                    exception.GetBaseException().Message);
            }
            finally
            {
                lock (Sync)
                {
                    _runtimeProvidersDirty = false;
                }
                refreshProviders = false;
            }
        }

        // Avatar nodes can exist in the lobby while the combat room is unavailable.  Refresh
        // them independently of the resource/rebuild gate so a received selection is visible
        // immediately instead of waiting for the run scene to finish loading.
        if (refreshProviders)
        {
            lock (Sync)
            {
                foreach (var playerId in pendingIcons)
                {
                    PendingIconRefreshes.Add(playerId);
                }
            }
        }
        foreach (var playerId in pendingIcons.Where(_ => !refreshProviders))
        {
            try
            {
                // A selection packet can arrive before the lobby/HUD scene has finished adding
                // its avatar nodes.  Keep the request queued until a real icon is refreshed;
                // otherwise the one-shot attempt leaves the base icon cached forever.
                if (!ContextualSkinControls.RefreshMultiplayerPlayerIcons(playerId))
                {
                    lock (Sync)
                    {
                        PendingIconRefreshes.Add(playerId);
                    }
                }
            }
            catch (Exception exception)
            {
                ModLog.Warn(
                    $"刷新联机玩家 {playerId} 的头像失败：" +
                    exception.GetBaseException().Message);
            }
        }

        foreach (var playerId in pendingTransforms)
        {
            var refreshed = false;
            try
            {
                refreshed = CharacterAppearanceRuntime.RefreshPlayerTransforms(playerId);
                if (!refreshed)
                {
                    // Transform packets may arrive before the combat room/creature nodes exist.
                    // Keep them queued until a real creature has been refreshed; removing the
                    // entry in the same tick made the first live update disappear permanently.
                    lock (Sync)
                    {
                        PendingTransformRefreshes.Add(playerId);
                    }
                }
            }
            catch (Exception exception)
            {
                ModLog.Warn(
                    $"刷新联机玩家 {playerId} 的外观参数失败：" +
                    exception.GetBaseException().Message);
            }
            if (refreshed)
            {
                lock (Sync)
                {
                    PendingTransformRefreshes.Remove(playerId);
                }
            }
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
                PendingTransformRefreshes.Remove(playerId);
                PendingIconRefreshes.Remove(playerId);
            }
        }
    }

    internal static void OnLocalTransformChanged(string groupId)
    {
        var service = _netService;
        if (service == null || !service.Type.IsMultiplayer() ||
            !TryGetLocalCharacter(out _, out var character) ||
            !ContextualSkinControls.MatchesGroupIdentity(
                groupId,
                character.Id.Entry,
                character.GetType().Name))
        {
            return;
        }

        if (MarkLocalTransformAdvertisementDirty())
        {
            ModLog.Info($"已检测到本机 {groupId} 的外观参数变更，等待广播给其他玩家。");
            // Character sliders can be used after the lobby node has left the tree.  Publish
            // once from the change callback as a fallback; the regular game-loop tick still
            // provides the low-frequency retry when a transport/resource is temporarily busy.
            // Without this path a combat scene that does not host MultiplayerSkinSyncNode could
            // leave the dirty flag set forever, making the local change look successful only on
            // this machine.
            FlushLocalTransformAdvertisement(0.05);
        }
    }

    internal static void OnLocalTransformChanged(Creature creature, string groupId)
    {
        var service = _netService;
        if (service == null || !service.Type.IsMultiplayer())
        {
            return;
        }

        // A few game versions populate Creature.Player one frame after the visual node.  The
        // local-character overload still has an authoritative lobby/run lookup, so do not drop
        // the edit merely because the creature owner link is not ready yet.
        var owner = creature.Player ?? creature.PetOwner;
        if (owner == null)
        {
            OnLocalTransformChanged(groupId);
            return;
        }

        if (owner.NetId != service.NetId ||
            !ContextualSkinControls.MatchesGroupIdentity(
                groupId,
                owner.Character.Id.Entry,
                owner.Character.GetType().Name))
        {
            return;
        }

        if (MarkLocalTransformAdvertisementDirty())
        {
            ModLog.Info($"已检测到本机 {groupId} 的外观参数变更，等待广播给其他玩家。");
            FlushLocalTransformAdvertisement(0.05);
        }
    }

    private static bool MarkLocalTransformAdvertisementDirty()
    {
        lock (Sync)
        {
            if (_localTransformAdvertisementDirty)
            {
                return false;
            }

            _localTransformAdvertisementDirty = true;
            return true;
        }
    }

    private static void FlushLocalTransformAdvertisement(double delta)
    {
        ulong localId;
        SkinChangerNetMessage message;
        var needsAdvertisement = false;
        lock (Sync)
        {
            _localTransformBroadcastCooldown = Math.Max(
                0,
                _localTransformBroadcastCooldown - delta);
            if (!_localTransformAdvertisementDirty ||
                _localTransformBroadcastCooldown > 0 ||
                _netService == null)
            {
                return;
            }
            localId = _netService.NetId;
            _localTransformAdvertisementDirty = false;
            _localTransformBroadcastCooldown = 0.05;
            if (!AdvertisedSelections.TryGetValue(localId, out message))
            {
                needsAdvertisement = true;
                message = default;
            }
        }

        if (needsAdvertisement)
        {
            // AttachToRun can race the first creation of the local Player. Recreate the base
            // advertisement here instead of silently dropping the first transform edit forever.
            RememberLocalAdvertisement();
            lock (Sync)
            {
                if (!AdvertisedSelections.TryGetValue(localId, out message))
                {
                    return;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(message.GroupId) ||
            string.IsNullOrWhiteSpace(message.OptionId))
        {
            return;
        }

        message.TransformManifest = SerializeTransformManifest(
            SkinService.GetSessionCharacterCombatTransforms(
                message.GroupId,
                message.OptionId));
        var transformSignature = message.GroupId + "\n" + message.OptionId + "\n" +
                                 message.TransformManifest;
        var shouldLogTransform = false;
        lock (Sync)
        {
            AdvertisedSelections[localId] = message;
            if (!string.Equals(_lastSentTransformSignature, transformSignature, StringComparison.Ordinal))
            {
                _lastSentTransformSignature = transformSignature;
                shouldLogTransform = true;
            }
        }
        // Keep one low-volume breadcrumb per actual parameter state.  This makes it possible to
        // distinguish "the slider never sent a packet" from "the peer received but did not apply
        // it" without logging every mouse-move frame.
        if (shouldLogTransform)
        {
            ModLog.Info(
                $"已广播本机 {message.GroupId} 的联机外观参数：{message.TransformManifest.Length} 字符，" +
                $"状态={transformSignature.GetHashCode():X8}。");
        }
        SendLocalAdvertisement();
    }

    internal static void OnLocalCharacterSelectionChanged(string groupId)
    {
        if (!TryGetLocalCharacter(out var playerNetId, out var character))
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

        // Prepare the sender's safe-resource description as soon as the skin changes.  The
        // client does not always execute the host-only ready-gate callback, so waiting until
        // both players press Ready could leave the peer with only an option ID and no download
        // metadata.  Packaging is asynchronous and does not download anything; the completed
        // fingerprint is sent again by OnLocalOnlineMetadataReady.
        RememberLocalAdvertisement(includeOnlineMetadata: true);
        SendLocalAdvertisement();
        // Changing a skin does not change StartRunLobbyPlayer.character, so the game's lobby
        // listener does not call NRemoteLobbyPlayer.RefreshVisuals.  Refresh the local row from
        // the normal process path instead of waiting for a character change packet.
        ContextualSkinControls.RefreshMultiplayerPlayerIcons(playerNetId);
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

    internal static void ResetConnectionState(bool clearOnlineCache = false)
    {
        DetachFromRun(
            refreshRuntimeProviders: clearOnlineCache,
            clearOnlineCache: clearOnlineCache);
    }

    /// <summary>
    /// Ends the actual multiplayer run (abandon, finish, or return to the main menu).  This is
    /// deliberately separate from a transport disconnect: Steam can disconnect while the room
    /// UI is being rebuilt, and that must not throw away the downloaded appearance package.
    /// </summary>
    internal static void EndMultiplayerRunSession()
    {
        // StartRunLobby.CleanUp(true) detaches the sync state before RunManager.CleanUp is
        // reached.  The temporary online package is intentionally kept across that transport
        // teardown, so include the cache marker here; otherwise abandoning a run leaves the old
        // provider mounted and the next lobby can show its skin even though its maps were cleared.
        if (_netService?.Type.IsMultiplayer() != true && !_inRun && !_hasLobbySession &&
            !OnlineSkinCache.HasActiveSession)
        {
            return;
        }

        DetachFromRun(
            refreshRuntimeProviders: true,
            clearOnlineCache: true);
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
            OnlineSkinCache.ClearBlockingFailures();
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
        if (service == null || !hostId.HasValue)
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
                    _readyGateActive = true;
                    _runReleaseCommitted = false;
                    _readyGateElapsed = 0;
                    _localReadyResolutionComplete = false;
                    _readyGateQuietElapsed = 0;
                }
                // Selection advertisements can arrive just before the probe. Re-evaluate them
                // now that the client gate is active; otherwise a missing manifest received in
                // that small ordering window would have already fallen back to the base skin and
                // the client would report "loaded" immediately.
                SkinChangerNetMessage[] knownSelections;
                lock (Sync)
                {
                    knownSelections = AdvertisedSelections.Values
                        .Where(selection => selection.PlayerNetId != service.NetId)
                        .ToArray();
                }
                foreach (var selection in knownSelections)
                {
                    TryMakeSelectionAvailable(selection);
                }
                MarkReadyResolutionActivity();
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
            !ValidateOptionalText(message.SafeResourceFingerprint) ||
            !ValidateOptionalText(message.OnlineFailure) ||
            message.SafeResourceManifest is { Length: > 65536 } ||
            message.SafeResourceBindings is { Length: > 65536 } ||
            message.TransformManifest is { Length: > 65536 } ||
            !TryParseTransformManifest(message.TransformManifest, message.GroupId, out _))
        {
            return;
        }

        RememberAdvertisement(message);
        OnlineSkinCache.DiscardPendingSelectionsForPlayer(message.PlayerNetId);
        var receivedTransformSignature = message.GroupId + "\n" + message.OptionId + "\n" +
                                         message.TransformManifest;
        var shouldLogTransform = false;
        lock (Sync)
        {
            if (!LastReceivedTransformSignatures.TryGetValue(message.PlayerNetId, out var previous) ||
                !previous.Equals(receivedTransformSignature, StringComparison.Ordinal))
            {
                LastReceivedTransformSignatures[message.PlayerNetId] = receivedTransformSignature;
                shouldLogTransform = !string.IsNullOrWhiteSpace(message.TransformManifest);
            }
        }
        if (shouldLogTransform)
        {
            ModLog.Info(
                $"已收到联机玩家 {message.PlayerNetId} 的外观参数：" +
                $"{message.TransformManifest.Length} 字符，状态={receivedTransformSignature.GetHashCode():X8}。");
        }
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

    private static string SerializeTransformManifest(
        IReadOnlyDictionary<string, CharacterCombatTransform> transforms) =>
        JsonSerializer.Serialize(transforms);

    private static bool TryParseTransformManifest(
        string? manifest,
        string groupId,
        out IReadOnlyDictionary<string, CharacterCombatTransform> transforms)
    {
        transforms = new Dictionary<string, CharacterCombatTransform>(
            StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(manifest))
        {
            return true;
        }
        if (manifest.Length > 65536)
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, CharacterCombatTransform>>(
                manifest);
            if (parsed == null || parsed.Count > 64)
            {
                return false;
            }

            var companionPrefix = groupId + "::companion::";
            var normalized = new Dictionary<string, CharacterCombatTransform>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var pair in parsed)
            {
                if (pair.Value == null || pair.Key.Length is 0 or > 512 ||
                    (!pair.Key.Equals(groupId, StringComparison.OrdinalIgnoreCase) &&
                     !pair.Key.StartsWith(companionPrefix, StringComparison.OrdinalIgnoreCase)) ||
                    !IsFinite(pair.Value))
                {
                    return false;
                }
                normalized[pair.Key] = SkinService.NormalizeCharacterCombatTransform(pair.Value);
            }
            transforms = normalized;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }

        static bool IsFinite(CharacterCombatTransform value) =>
            float.IsFinite(value.Scale) &&
            float.IsFinite(value.OffsetX) &&
            float.IsFinite(value.OffsetY) &&
            float.IsFinite(value.HealthBarScale) &&
            float.IsFinite(value.HealthBarOffsetX) &&
            float.IsFinite(value.HealthBarOffsetY) &&
            float.IsFinite(value.IntentScale) &&
            float.IsFinite(value.IntentOffsetX) &&
            float.IsFinite(value.IntentOffsetY) &&
            float.IsFinite(value.SelectionReticleScale) &&
            float.IsFinite(value.SelectionReticleOffsetX) &&
            float.IsFinite(value.SelectionReticleOffsetY);
    }

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
                PendingTransformRefreshes.Remove(message.PlayerNetId);
                PendingIconRefreshes.Remove(message.PlayerNetId);
            }
            return;
        }

        if (!PlayerMatchesCharacterSelection(message) ||
            !TryParseTransformManifest(
                message.TransformManifest,
                message.GroupId,
                out var transforms))
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
                     OnlineSkinCache.IsWaitingForDownloadMetadata(message) &&
                     IsReadyGateActive())
            {
                OnlineSkinCache.ReportWaitingForMetadata(message);
                MarkReadyResolutionActivity();
            }
            else if (message.OptionId != SkinCatalog.BaseOptionId &&
                     !string.IsNullOrWhiteSpace(message.OnlineFailure) &&
                     IsReadyGateActive())
            {
                OnlineSkinCache.ReportMissingMetadata(message);
                ModLog.Info(
                    $"联机玩家 {message.PlayerNetId} 的皮肤 {message.OptionId} " +
                    "没有携带可下载资源信息，暂时显示原皮。");
            }
            else if (message.OptionId != SkinCatalog.BaseOptionId &&
                     !OnlineSkinCache.HasDownloadMetadata(message) &&
                     IsReadyGateActive())
            {
                // A non-base selection without a downloadable manifest must not be treated as
                // an already-complete request.  That made the ready gate say “loaded” instantly
                // while the remote player silently fell back to the original skin.  Surface the
                // same explicit failure used for private/local-only skins and keep the gate open
                // until the player acknowledges it.
                OnlineSkinCache.ReportMissingMetadata(message);
                ModLog.Info(
                    $"联机玩家 {message.PlayerNetId} 的皮肤 {message.OptionId} " +
                    "缺少可验证的在线资源清单，已暂停开局等待确认。");
                MarkReadyResolutionActivity();
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

        var ownerSelectedBase = message.OptionId.Equals(
            SkinCatalog.BaseOptionId,
            StringComparison.OrdinalIgnoreCase);
        var effectiveIsBase = effectiveOptionId.Equals(
            SkinCatalog.BaseOptionId,
            StringComparison.OrdinalIgnoreCase);
        var ownerAppearanceLoaded = allowRemoteSkin &&
                                    selectionAvailable &&
                                    ownerSelectedBase == effectiveIsBase;
        if (!ownerAppearanceLoaded)
        {
            string? fallbackOptionId;
            lock (Sync)
            {
                LocalFallbackSelections.TryGetValue(message.PlayerNetId, out fallbackOptionId);
            }
            if (!string.IsNullOrWhiteSpace(fallbackOptionId) &&
                SkinService.TryBuildSessionCharacterSelection(
                    message.GroupId,
                    fallbackOptionId,
                    out var fallbackSelections))
            {
                effectiveOptionId = fallbackOptionId;
                selectionOverrides = fallbackSelections;
                selectionAvailable = true;
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
                    PendingTransformRefreshes.Remove(message.PlayerNetId);
                    PendingIconRefreshes.Add(message.PlayerNetId);
                    _runtimeProvidersDirty = true;
                }
                return;
            }
            effectiveOptionId = SkinCatalog.BaseOptionId;
        }

        var appearanceChanged = false;
        lock (Sync)
        {
            if (ownerAppearanceLoaded)
            {
                LocalFallbackSelections.Remove(message.PlayerNetId);
                foreach (var key in LocalFallbackTransforms.Keys
                             .Where(key => key.PlayerId == message.PlayerNetId)
                             .ToArray())
                {
                    LocalFallbackTransforms.Remove(key);
                }
            }

            var next = new SessionCharacterSelection(
                message.CharacterId,
                message.GroupId,
                effectiveOptionId,
                selectionOverrides,
                transforms,
                ownerAppearanceLoaded);
            appearanceChanged = !AvailableSelections.TryGetValue(
                                        message.PlayerNetId,
                                        out var previous) ||
                                    !previous.CharacterId.Equals(
                                        next.CharacterId,
                                        StringComparison.OrdinalIgnoreCase) ||
                                    !previous.GroupId.Equals(
                                        next.GroupId,
                                        StringComparison.OrdinalIgnoreCase) ||
                                    !previous.OptionId.Equals(
                                        next.OptionId,
                                        StringComparison.OrdinalIgnoreCase) ||
                                    previous.OwnerAppearanceLoaded != next.OwnerAppearanceLoaded ||
                                    !DictionaryEquals(
                                        previous.SelectionOverrides,
                                        next.SelectionOverrides);
            AvailableSelections[message.PlayerNetId] = next;
            if (appearanceChanged)
            {
                PendingRefreshes.Add(message.PlayerNetId);
                PendingTransformRefreshes.Remove(message.PlayerNetId);
                PendingIconRefreshes.Add(message.PlayerNetId);
                _runtimeProvidersDirty = true;
            }
            else
            {
                PendingTransformRefreshes.Add(message.PlayerNetId);
            }
        }

        var appliedTransformSignature = message.GroupId + "\n" + effectiveOptionId + "\n" +
                                        message.TransformManifest;
        var transformChanged = false;
        lock (Sync)
        {
            if (!LastAppliedTransformSignatures.TryGetValue(
                    message.PlayerNetId,
                    out var previous) ||
                !previous.Equals(appliedTransformSignature, StringComparison.Ordinal))
            {
                LastAppliedTransformSignatures[message.PlayerNetId] = appliedTransformSignature;
                transformChanged = !string.IsNullOrWhiteSpace(message.TransformManifest);
            }
        }
        if (appearanceChanged || transformChanged)
        {
            ModLog.Info(
                $"已应用联机玩家 {message.PlayerNetId} 的角色 {message.CharacterId}：" +
                $"皮肤={effectiveOptionId}，参数项={transforms.Count}，" +
                $"头像刷新={(appearanceChanged ? "等待节点刷新" : "仅参数刷新")}。 ");
        }

        static bool DictionaryEquals(
            IReadOnlyDictionary<string, string> left,
            IReadOnlyDictionary<string, string> right) =>
            left.Count == right.Count && left.All(pair =>
                right.TryGetValue(pair.Key, out var value) &&
                value.Equals(pair.Value, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsReadyGateActive()
    {
        lock (Sync)
        {
            return _readyGateActive;
        }
    }

    internal static bool IsReadyGateActiveForOnlineCache() => IsReadyGateActive();

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
        message.TransformManifest = SerializeTransformManifest(
            SkinService.GetSessionCharacterCombatTransforms(
                message.GroupId,
                message.OptionId));
        if (includeOnlineMetadata &&
            !message.OptionId.Equals(SkinCatalog.BaseOptionId, StringComparison.OrdinalIgnoreCase))
        {
            var state = OnlineSkinCache.TryDescribeLocalSelection(
                message.GroupId,
                message.OptionId,
                out var source,
                out var failureDetail);
            if (source != null)
            {
                message.ProviderId = source.ProviderId;
                message.WorkshopItemId = source.WorkshopItemId;
                message.SafeResourceFingerprint = source.SafeResourceFingerprint;
                message.SafeResourceManifest = source.SafeResourceManifest;
                message.SafeResourceBindings = source.SafeResourceBindings;
            }

            // Keep the sender-side reason visible.  A receiver can only report that metadata is
            // missing; without this line it is impossible to tell whether the provider has no
            // Steam source, was still being packaged, or failed the safe-resource checks.
            if (state != OnlineSkinDescriptionState.Ready)
            {
                ModLog.Info(
                    $"本地联机皮肤描述：分组={message.GroupId}，选项={message.OptionId}，" +
                    $"状态={state}，提供者={message.ProviderId}，工坊={message.WorkshopItemId}，" +
                    $"清单={message.SafeResourceManifest?.Length ?? 0} 字符，映射={message.SafeResourceBindings?.Length ?? 0} 字符" +
                    (string.IsNullOrWhiteSpace(failureDetail) ? string.Empty : $"，原因={failureDetail}") +
                    "。 ");
            }
            if (state is OnlineSkinDescriptionState.Unavailable or OnlineSkinDescriptionState.Failed)
            {
                message.ProviderId = string.IsNullOrWhiteSpace(message.ProviderId)
                    ? message.OptionId
                    : message.ProviderId;
                message.OnlineFailure = failureDetail;
                OnlineSkinCache.ReportLocalDescriptionFailure(
                    message.GroupId,
                    message.OptionId,
                    message.ProviderId,
                    message.WorkshopItemId,
                    failureDetail);
            }
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
                (current.WorkshopItemId != 0 &&
                 current.WorkshopItemId != message.WorkshopItemId) ||
                (!string.IsNullOrWhiteSpace(current.SafeResourceFingerprint) &&
                 !current.SafeResourceFingerprint.Equals(
                     message.SafeResourceFingerprint,
                     StringComparison.OrdinalIgnoreCase)))
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

    internal static bool TryGetPlayerCharacter(ulong playerNetId, out CharacterModel character)
    {
        if (TryGetLobbyCharacter(playerNetId, out character))
        {
            return true;
        }

        return CharacterAppearanceRuntime.TryGetPlayerCharacter(playerNetId, out character);
    }

    private static bool PlayerMatchesCharacterSelection(SkinChangerNetMessage message)
    {
        if (TryGetLobbyCharacter(message.PlayerNetId, out var character))
        {
            return character.Id.Entry.Equals(
                       message.CharacterId,
                       StringComparison.OrdinalIgnoreCase) &&
                   ContextualSkinControls.MatchesGroupIdentity(
                       message.GroupId,
                       character.Id.Entry,
                       character.GetType().Name);
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
            if (AdvertisedSelections.TryGetValue(message.PlayerNetId, out var previous) &&
                SameAdvertisedSelection(previous, message) &&
                string.IsNullOrWhiteSpace(message.OnlineFailure) &&
                !OnlineSkinCache.HasDownloadMetadata(message) &&
                OnlineSkinCache.HasDownloadMetadata(previous))
            {
                // Ordinary lobby snapshots intentionally omit the downloadable resource
                // description. They can arrive after the richer ready-gate advertisement, so
                // replacing the dictionary entry outright made a completed download impossible
                // to attach to the player. Preserve the richer description for the same exact
                // selection while still accepting the newest transform manifest.
                message.ProviderId = previous.ProviderId;
                message.WorkshopItemId = previous.WorkshopItemId;
                message.SafeResourceFingerprint = previous.SafeResourceFingerprint;
                message.SafeResourceManifest = previous.SafeResourceManifest;
                message.SafeResourceBindings = previous.SafeResourceBindings;
                message.OnlineFailure = previous.OnlineFailure;
            }
            AdvertisedSelections[message.PlayerNetId] = message;
        }
    }

    private static bool SameAdvertisedSelection(
        SkinChangerNetMessage left,
        SkinChangerNetMessage right) =>
        left.PlayerNetId == right.PlayerNetId &&
        string.Equals(left.CharacterId, right.CharacterId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.GroupId, right.GroupId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.OptionId, right.OptionId, StringComparison.OrdinalIgnoreCase);

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
            // The host may not have processed the capability trailer yet (some networking Mods
            // replace the initial join packet).  Sending the reserved Skin Changer envelope is
            // safe and lets HandleMessage establish capability from the message itself; gating
            // this send on IsCapable made every later avatar/transform update disappear on those
            // connections.
            if (hostId.HasValue)
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
            LocalFallbackSelections.Remove(peerId);
            foreach (var key in LocalFallbackTransforms.Keys
                         .Where(key => key.PlayerId == peerId)
                         .ToArray())
            {
                LocalFallbackTransforms.Remove(key);
            }
            PendingRefreshes.Remove(peerId);
            PendingTransformRefreshes.Remove(peerId);
            PendingIconRefreshes.Remove(peerId);
        }
    }

    private static void OnDisconnected(NetErrorInfo _) =>
        DetachFromRun(refreshRuntimeProviders: false);

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
        refreshRuntimeProviders: false);
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

[HarmonyPatch(typeof(NRun), nameof(NRun._Process))]
internal static class MultiplayerSkinRunTickPatch
{
    // NRun is the persistent process owner for the live combat/map scene.  Keep this hook even
    // when the optional sync node is recreated during a scene hand-off, so queued transform and
    // avatar updates cannot wait on a child _Process callback that no longer exists.
    private static void Postfix(double delta) => MultiplayerSkinSync.Tick(delta);
}

[HarmonyPatch(typeof(StartRunLobby), "BeginRunForAllPlayersIfAllReady")]
internal static class MultiplayerSkinReadyGatePatch
{
    private static bool Prefix(StartRunLobby __instance) =>
        MultiplayerSkinSync.ShouldAllowBeginRun(__instance);
}

[HarmonyPatch(typeof(StartRunLobby), nameof(StartRunLobby.CleanUp))]
internal static class MultiplayerSkinLobbyCleanupPatch
{
    private static void Prefix(bool disconnectSession)
    {
        // CleanUp(false) is the normal lobby-to-run transition and must retain the remote
        // providers until the run has built its creatures.  CleanUp(true) only tears down the
        // lobby transport; the temporary package is intentionally retained until a run ends or
        // a new multiplayer lobby starts.
        if (disconnectSession)
        {
            MultiplayerSkinSync.ResetConnectionState();
        }
    }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
internal static class MultiplayerSkinRunCleanupPatch
{
    private static void Prefix() => MultiplayerSkinSync.EndMultiplayerRunSession();
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
    private static void Prefix() => MultiplayerSkinSync.ResetConnectionState(clearOnlineCache: true);
}

[HarmonyPatch]
internal static class SkinChangerHostConnectionResetPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(NetHostGameService), nameof(NetHostGameService.StartSteamHost));
        yield return AccessTools.Method(typeof(NetHostGameService), nameof(NetHostGameService.StartENetHost));
    }

    private static void Prefix() => MultiplayerSkinSync.ResetConnectionState(clearOnlineCache: true);
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
