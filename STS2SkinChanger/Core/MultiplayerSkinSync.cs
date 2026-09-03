using System.Reflection;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
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
    public string SourceOptionManifest;
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
        writer.WriteString(SourceOptionManifest ?? "[]");
        writer.WriteString(TransformManifest ?? string.Empty);
    }

    public void Deserialize(PacketReader reader)
    {
        ProtocolVersion = reader.ReadByte();
        Kind = (SkinSyncMessageKind)reader.ReadByte();
        PlayerNetId = reader.ReadULong();
        CharacterId = reader.ReadString();
        GroupId = reader.ReadString();
        OptionId = reader.ReadString();
        SourceOptionManifest = reader.ReadString();
        TransformManifest = reader.ReadString();
        ProviderId = string.Empty;
        WorkshopItemId = 0;
        SafeResourceFingerprint = string.Empty;
        SafeResourceManifest = string.Empty;
        SafeResourceBindings = string.Empty;
        OnlineFailure = string.Empty;
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
    // Protocol 9 transmits the ordered raw sources behind a saved composition. Receivers only
    // resolve sources already installed locally; no Workshop download or remote cache is used.
    internal const byte ProtocolVersion = 9;
    internal const int ReservedMessageId = 254;
    private const int MaximumSourceOptionCount = 64;
    private const int MaximumSourceOptionManifestLength = 32768;

    private static readonly byte[] CapabilityMagic =
        [0x47, 0x53, 0x43, 0x41, 0x50, 0x30, 0x39, 0x21]; // GSCAP09!
    private static readonly HashSet<ulong> CapablePeers = [];
    private static readonly Dictionary<ulong, SkinChangerNetMessage> AdvertisedSelections = [];
    private static readonly Dictionary<ulong, SessionCharacterSelection> AvailableSelections = [];
    private static readonly Dictionary<(ulong PlayerId, string GroupId), string>
        LocalFallbackSelections = [];
    private static readonly Dictionary<(ulong PlayerId, string TransformKey), CharacterCombatTransform>
        LocalFallbackTransforms = [];
    private static readonly HashSet<ulong> PendingRefreshes = [];
    private static readonly HashSet<ulong> PendingTransformRefreshes = [];
    private static readonly HashSet<ulong> PendingIconRefreshes = [];
    private static readonly Dictionary<ulong, string> LastReceivedTransformSignatures = [];
    private static readonly Dictionary<ulong, string> LastAppliedTransformSignatures = [];
    private static readonly HashSet<string> MissingInstalledSkinWarnings =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Sync = new();

    [ThreadStatic]
    private static Stack<IReadOnlyDictionary<string, string>>? _selectionScopes;

    private static INetGameService? _netService;
    private static StartRunLobby? _lobby;
    private static MessageHandlerDelegate<SkinChangerNetMessage>? _messageHandler;
    private static double _snapshotElapsed;
    private static int _snapshotStage;
    private static bool _runtimeProvidersDirty;
    private static bool _localTransformAdvertisementDirty;
    private static double _localTransformBroadcastCooldown;
    private static string? _lastSentTransformSignature;
    private static bool _inRun;
    private static bool _hasLobbySession;
    private static StartRunLobby? _suspendedLobby;
    private static bool _resumeRunWhenEnabled;
    // The combat scene exits before the next StartRunLobby is attached, and both scenes reuse
    // the same Steam net service.  Keep this hand-off marker separate from _inRun so the lobby
    // attach can still invalidate the previous round's temporary providers and advertisements.
    private static bool _needsLobbyRoundReset;

    private static bool ShouldApplyRemoteAppearance()
    {
        var service = _netService;
        if (service?.Type.IsMultiplayer() != true)
        {
            return false;
        }

        return MultiplayerSkinSyncParticipationPolicy.Resolve(
            SkinService.ShouldSendMultiplayerSkinChanges(),
            SkinService.ShouldReceiveMultiplayerSkinChanges(),
            isMultiplayer: true).ApplyRemoteAppearance;
    }

    private static bool ShouldParticipate() =>
        SkinService.ShouldSendMultiplayerSkinChanges() ||
        SkinService.ShouldReceiveMultiplayerSkinChanges();

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
        if (!ShouldApplyRemoteAppearance())
        {
            return [];
        }

        lock (Sync)
        {
            return AvailableSelections.Values
                .Select(selection => selection.SelectionOverrides)
                .ToArray();
        }
    }

    internal static string GetSelectionForCreature(Creature creature, string groupId)
    {
        if (!ShouldApplyRemoteAppearance())
        {
            return SkinService.Config.GetSelection(groupId);
        }

        var player = creature.Player ?? creature.PetOwner;
        if (player == null)
        {
            return SkinService.Config.GetSelection(groupId);
        }

        if (TryGetPlayerSelectionMap(player.NetId, player.Character, out var selections) &&
            selections.TryGetValue(groupId, out var optionId))
        {
            return optionId;
        }

        var service = _netService;
        if (service != null && service.Type.IsMultiplayer() && player.NetId != service.NetId)
        {
            // A remote player whose selection has not arrived yet must never inherit this
            // machine's persistent choice for the same character.
            return SkinCatalog.BaseOptionId;
        }

        return SkinService.Config.GetSelection(groupId);
    }

    internal static bool CanEditSkinForCreature(Creature creature)
    {
        if (!ShouldApplyRemoteAppearance())
        {
            return true;
        }

        var owner = creature.Player ?? creature.PetOwner;
        var service = _netService;
        if (owner == null || service == null || !service.Type.IsMultiplayer())
        {
            return true;
        }

        if (owner.NetId == service.NetId)
        {
            // The local player may switch their own skin during a multiplayer run. The normal
            // persistent selection path rebuilds only through per-player scopes and advertises
            // the new choice to peers immediately.
            return true;
        }

        lock (Sync)
        {
            // The owner remains authoritative whenever their selected option exists locally.
            // If it is unavailable, this client may choose a local-only fallback for that remote
            // player without changing the owner's saved selection or any other player's skin.
            return !AvailableSelections.TryGetValue(owner.NetId, out var selection) ||
                   !selection.OwnerAppearanceLoaded;
        }
    }

    internal static bool CanEditLocalPlayerSkinInRun() => true;

    internal static void RequestIconRefresh(ulong playerNetId)
    {
        lock (Sync)
        {
            PendingIconRefreshes.Add(playerNetId);
        }
    }

    internal static bool CanEditTransformForCreature(Creature creature)
    {
        if (!ShouldApplyRemoteAppearance())
        {
            return true;
        }

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
        if (!ShouldApplyRemoteAppearance())
        {
            return false;
        }

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
        if (!ShouldApplyRemoteAppearance())
        {
            transform = null!;
            return false;
        }

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
            // The selected skin may be unavailable on this machine, in which case that player is
            // displayed with the original model. Their synchronized model/UI parameters remain
            // useful and continue to apply to that original-model fallback.
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
            LocalFallbackSelections[(owner.NetId, groupId)] = optionId;
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

        if (!TryGetPlayerSelectionMap(player.NetId, player.Character, out var selections))
        {
            return null;
        }

        _selectionScopes ??= new Stack<IReadOnlyDictionary<string, string>>();
        _selectionScopes.Push(selections);
        return new SelectionScope();
    }

    internal static IDisposable? BeginCreatureRuntimeScope(Creature creature)
    {
        var selectionScope = BeginCreatureSelectionScope(creature);
        if (selectionScope == null)
        {
            return null;
        }

        try
        {
            var modelId = creature.Player?.Character.Id.Entry ?? creature.Monster?.Id.Entry;
            var modelTypeName = creature.Player?.Character.GetType().Name ??
                                creature.Monster?.GetType().Name;
            var group = modelId == null
                ? null
                : ContextualSkinControls.FindGroup(modelId, modelTypeName);
            if (group == null)
            {
                return selectionScope;
            }

            var scenePath = creature.Player != null
                ? ContextualSkinControls.CanonicalScenePath(
                    "creature_visuals/" + modelId!.ToLowerInvariant())
                : ContextualSkinControls.GetMonsterVisualsPath(creature.Monster!);
            var resourceScope = SkinService.BeginRuntimeResourceScope(group.Id, scenePath);
            return new CombinedScope(selectionScope, resourceScope);
        }
        catch
        {
            selectionScope.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Temporarily selects a remote player's complete visual map while the game builds a UI
    /// object for that player (for example the multiplayer health-bar avatar). CharacterModel's
    /// icon getters do not receive a Player argument, so the caller must provide this context.
    /// </summary>
    internal static IDisposable? BeginPlayerSelectionScope(ulong playerNetId)
    {
        if (!TryGetPlayerSelectionMap(playerNetId, character: null, out var selections))
        {
            return null;
        }

        _selectionScopes ??= new Stack<IReadOnlyDictionary<string, string>>();
        _selectionScopes.Push(selections);
        return new SelectionScope();
    }

    /// <summary>
    /// Keeps both a player's selected option map and that option's private Godot resource overlay
    /// active while a non-combat character scene is loaded and attached to the tree. Merchant and
    /// rest-site visuals are created from a shared CharacterModel, so selection-only scoping is not
    /// enough for skins with their own skeleton dependencies or managed Godot scripts.
    /// </summary>
    internal static IDisposable? BeginPlayerRuntimeScope(Player player, string scenePath)
    {
        var selectionScope = BeginPlayerSelectionScope(player.NetId);
        if (selectionScope == null)
        {
            return null;
        }

        try
        {
            var group = ContextualSkinControls.FindGroup(
                player.Character.Id.Entry,
                player.Character.GetType().Name);
            if (group == null)
            {
                return selectionScope;
            }

            var resourceScope = SkinService.BeginRuntimeResourceScope(group.Id, scenePath);
            return new CombinedScope(selectionScope, resourceScope);
        }
        catch
        {
            selectionScope.Dispose();
            throw;
        }
    }

    private static bool TryGetPlayerSelectionMap(
        ulong playerNetId,
        CharacterModel? character,
        out IReadOnlyDictionary<string, string> selections)
    {
        if (!ShouldApplyRemoteAppearance())
        {
            selections = null!;
            return false;
        }

        var resolvedCharacter = character;
        if (resolvedCharacter == null &&
            TryGetPlayerCharacter(playerNetId, out var discoveredCharacter))
        {
            resolvedCharacter = discoveredCharacter;
        }

        lock (Sync)
        {
            if (AvailableSelections.TryGetValue(playerNetId, out var selection) &&
                (resolvedCharacter == null ||
                 selection.CharacterId.Equals(
                     resolvedCharacter.Id.Entry,
                     StringComparison.OrdinalIgnoreCase) &&
                 ContextualSkinControls.MatchesGroupIdentity(
                     selection.GroupId,
                     resolvedCharacter.Id.Entry,
                     resolvedCharacter.GetType().Name)))
            {
                selections = selection.SelectionOverrides;
                return true;
            }
        }

        var service = _netService;
        if (service == null || !service.Type.IsMultiplayer() ||
            playerNetId == service.NetId || resolvedCharacter == null)
        {
            selections = null!;
            return false;
        }

        var group = ContextualSkinControls.FindGroup(
            resolvedCharacter.Id.Entry,
            resolvedCharacter.GetType().Name);
        if (group != null && SkinService.TryBuildSessionCharacterSelection(
                group.Id,
                SkinCatalog.BaseOptionId,
                out selections))
        {
            return true;
        }

        selections = null!;
        return false;
    }

    internal static void AttachToRun()
    {
        var service = RunManager.Instance.NetService;
        _lobby = null;
        _needsLobbyRoundReset = false;
        var participation = MultiplayerSkinSyncParticipationPolicy.Resolve(
            SkinService.ShouldSendMultiplayerSkinChanges(),
            SkinService.ShouldReceiveMultiplayerSkinChanges(),
            service.Type.IsMultiplayer());
        if (!participation.AttachTransport)
        {
            _suspendedLobby = null;
            _resumeRunWhenEnabled = service.Type.IsMultiplayer();
            return;
        }

        _suspendedLobby = null;
        _resumeRunWhenEnabled = false;
        AttachToService(service, "对局");
        _inRun = true;
        RememberLocalAdvertisement();
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
        var participation = MultiplayerSkinSyncParticipationPolicy.Resolve(
            SkinService.ShouldSendMultiplayerSkinChanges(),
            SkinService.ShouldReceiveMultiplayerSkinChanges(),
            service.Type.IsMultiplayer());
        if (!participation.AttachTransport)
        {
            _suspendedLobby = service.Type.IsMultiplayer() ? lobby : null;
            _resumeRunWhenEnabled = false;
            return;
        }

        _suspendedLobby = null;
        _resumeRunWhenEnabled = false;
        var changedLobby = !ReferenceEquals(_lobby, lobby);
        // The game keeps the same Steam net service while returning from a run to a new
        // character-select lobby. Clear the previous round's per-player selection maps at this
        // boundary even when AttachToService does not run again.
        var resetRound = _needsLobbyRoundReset || (changedLobby && _hasLobbySession);
        if (resetRound)
        {
            ResetRoundStateForLobby();
        }
        _needsLobbyRoundReset = false;

        AttachToService(service, "联机选角");
        _lobby = lobby;
        _hasLobbySession = true;
        _inRun = false;
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
            MissingInstalledSkinWarnings.Clear();
            _runtimeProvidersDirty = false;
            _localTransformAdvertisementDirty = false;
            _localTransformBroadcastCooldown = 0;
            _lastSentTransformSignature = null;
        }

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
        SkinService.ClearSessionCharacterCompositions();

        _snapshotElapsed = 0;
        _snapshotStage = 0;
        ModLog.Info("已进入新的联机选角回合；上一回合的玩家皮肤映射已清理。");
    }

    private static void AttachToService(INetGameService service, string stage)
    {
        if (!MultiplayerSkinSyncParticipationPolicy.Resolve(
                SkinService.ShouldSendMultiplayerSkinChanges(),
                SkinService.ShouldReceiveMultiplayerSkinChanges(),
                service.Type.IsMultiplayer()).AttachTransport)
        {
            return;
        }

        if (ReferenceEquals(service, _netService))
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

        // A lobby/run scene hand-off reuses the transport. Mark it so the next actual lobby can
        // clear the preceding round's per-player selection state.
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
            MissingInstalledSkinWarnings.Clear();
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

        if (hadRemoteSelections && refreshRuntimeProviders)
        {
            try
            {
                SkinService.RefreshSessionRuntimeProviders();
            }
            catch (Exception exception)
            {
                ModLog.Warn(
                    "清理联机皮肤运行时失败：" +
                    exception.GetBaseException().Message);
            }
        }
        SkinService.ClearSessionCharacterCompositions();

    }

    internal static void Tick(double delta)
    {
        if (!ShouldParticipate())
        {
            return;
        }

        var service = _netService;
        if (service == null || !service.IsConnected)
        {
            return;
        }

        FlushLocalTransformAdvertisement(delta);

        _snapshotElapsed += delta;
        if ((_snapshotStage == 0 && _snapshotElapsed >= 0.75) ||
            (_snapshotStage == 1 && _snapshotElapsed >= 3.0))
        {
            _snapshotStage++;
            BroadcastKnownSelections();
        }

        RetryAdvertisementsWaitingForPlayerIdentity();

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
        if (!SkinService.ShouldSendMultiplayerSkinChanges())
        {
            return false;
        }

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
                                 message.SourceOptionManifest + "\n" +
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

        RememberLocalAdvertisement();
        SendLocalAdvertisement();
        // Changing a skin does not change StartRunLobbyPlayer.character, so the game's lobby
        // listener does not call NRemoteLobbyPlayer.RefreshVisuals.  Refresh the local row from
        // the normal process path instead of waiting for a character change packet.
        ContextualSkinControls.RefreshMultiplayerPlayerIcons(playerNetId);
    }

    internal static void OnReceivePreferenceChanged(bool enabled)
    {
        ulong[] remotePlayerIds;
        SkinChangerNetMessage[] advertisements;
        lock (Sync)
        {
            remotePlayerIds = AvailableSelections.Keys
                .Where(playerId => playerId != _netService?.NetId)
                .ToArray();
            advertisements = AdvertisedSelections.Values
                .Where(message => message.PlayerNetId != _netService?.NetId)
                .ToArray();
            if (!enabled)
            {
                foreach (var playerId in remotePlayerIds)
                {
                    AvailableSelections.Remove(playerId);
                    PendingRefreshes.Remove(playerId);
                    PendingTransformRefreshes.Remove(playerId);
                    PendingIconRefreshes.Remove(playerId);
                }
                _runtimeProvidersDirty = true;
            }
        }

        if (enabled)
        {
            foreach (var advertisement in advertisements)
            {
                try
                {
                    TryMakeSelectionAvailable(advertisement);
                }
                catch (Exception exception)
                {
                    ModLog.Warn(
                        $"重新接收联机玩家 {advertisement.PlayerNetId} 的外观失败：" +
                        exception.GetBaseException().Message);
                }
            }
            return;
        }

        try
        {
            SkinService.ClearSessionCharacterCompositions();
            SkinService.RefreshSessionRuntimeProviders();
        }
        catch (Exception exception)
        {
            ModLog.Warn(
                "关闭接收皮肤改变时恢复本机资源失败：" +
                exception.GetBaseException().Message);
        }
        foreach (var playerId in remotePlayerIds)
        {
            try
            {
                ContextualSkinControls.RefreshMultiplayerPlayerIcons(playerId);
                if (_inRun && CharacterAppearanceRuntime.CanApplySelectionImmediately())
                {
                    CharacterAppearanceRuntime.RefreshPlayerAppearance(playerId);
                }
            }
            catch (Exception exception)
            {
                ModLog.Warn(
                    $"关闭接收皮肤改变后恢复玩家 {playerId} 的本机外观失败：" +
                    exception.GetBaseException().Message);
            }
        }
    }

    internal static void OnDirectionPreferenceChanged()
    {
        var enabled = ShouldParticipate();
        if (enabled)
        {
            var lobbyToResume = _suspendedLobby;
            var runToResume = _resumeRunWhenEnabled;
            _suspendedLobby = null;
            _resumeRunWhenEnabled = false;

            if (runToResume && NRun.Instance != null)
            {
                AttachToRun();
                ModLog.Info("已重新启用本局的联机皮肤同步。");
                return;
            }
            if (lobbyToResume != null)
            {
                AttachToLobby(lobbyToResume);
                ModLog.Info("已重新启用当前房间的联机皮肤同步。");
                return;
            }

            if (_netService != null && SkinService.ShouldSendMultiplayerSkinChanges())
            {
                RememberLocalAdvertisement();
                SendLocalAdvertisement();
            }
            ModLog.Info("联机皮肤发送或接收已启用；将在当前或下次联机房间生效。");
            return;
        }

        var currentLobby = _lobby;
        var wasInRun = _inRun;
        ulong[] remotePlayerIds;
        lock (Sync)
        {
            remotePlayerIds = AvailableSelections.Keys.ToArray();
        }

        _suspendedLobby = currentLobby;
        _resumeRunWhenEnabled = wasInRun;
        DetachFromRun(
            clearCapabilities: true,
            refreshRuntimeProviders: true,
            clearOnlineCache: false);
        OnlineSkinCache.OnRemoteSkinLoadingPreferenceChanged(enabled: false);

        foreach (var playerId in remotePlayerIds)
        {
            try
            {
                ContextualSkinControls.RefreshMultiplayerPlayerIcons(playerId);
                if (wasInRun && CharacterAppearanceRuntime.CanApplySelectionImmediately())
                {
                    CharacterAppearanceRuntime.RefreshPlayerAppearance(playerId);
                }
            }
            catch (Exception exception)
            {
                ModLog.Warn(
                    $"关闭联机同步后恢复玩家 {playerId} 的本机外观失败：" +
                    exception.GetBaseException().Message);
            }
        }

        ModLog.Info("皮肤改变的发送与接收均已关闭；不会再修改握手包或交换玩家外观。");
    }

    internal static void BroadcastLocalFallbackBeforeDisablingSend()
    {
        if (_netService?.Type.IsMultiplayer() != true)
        {
            return;
        }

        RememberLocalAdvertisement(baseOnly: true);
        SendLocalAdvertisement(force: true);
    }

    internal static void ResetConnectionState(bool clearOnlineCache = false)
    {
        _suspendedLobby = null;
        _resumeRunWhenEnabled = false;
        DetachFromRun(
            refreshRuntimeProviders: clearOnlineCache,
            clearOnlineCache: clearOnlineCache);
    }

    /// <summary>
    /// Ends the actual multiplayer run (abandon, finish, or return to the main menu). This is
    /// deliberately separate from a transport disconnect because the room UI can be rebuilt
    /// while the same run continues.
    /// </summary>
    internal static void EndMultiplayerRunSession()
    {
        _suspendedLobby = null;
        _resumeRunWhenEnabled = false;
        if (_netService?.Type.IsMultiplayer() != true && !_inRun && !_hasLobbySession)
        {
            return;
        }

        DetachFromRun(
            refreshRuntimeProviders: true,
            clearOnlineCache: true);
    }


    internal static void MarkPeerCapable(ulong peerId, byte protocolVersion)
    {
        if (!ShouldParticipate())
        {
            return;
        }

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
        if (!MultiplayerSkinSyncParticipationPolicy.Resolve(
                SkinService.ShouldSendMultiplayerSkinChanges(),
                SkinService.ShouldReceiveMultiplayerSkinChanges(),
                isMultiplayer: true).ReadCapabilityTrailer)
        {
            return false;
        }

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
        if (!MultiplayerSkinSyncParticipationPolicy.Resolve(
                SkinService.ShouldSendMultiplayerSkinChanges(),
                SkinService.ShouldReceiveMultiplayerSkinChanges(),
                isMultiplayer: true).WriteCapabilityTrailer)
        {
            return;
        }

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
        if (!ShouldParticipate())
        {
            return;
        }

        var service = _netService;
        if (service == null || message.ProtocolVersion != ProtocolVersion ||
            message.Kind != SkinSyncMessageKind.CharacterSelection)
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
            !TryParseSourceOptionManifest(message.SourceOptionManifest, out _) ||
            message.TransformManifest is { Length: > 65536 } ||
            !TryParseTransformManifest(message.TransformManifest, message.GroupId, out _))
        {
            return;
        }

        RememberAdvertisement(message);
        var receivedTransformSignature = message.GroupId + "\n" + message.OptionId + "\n" +
                                         message.SourceOptionManifest + "\n" +
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
    }

    private static bool ValidateText(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 512;

    private static string SerializeTransformManifest(
        IReadOnlyDictionary<string, CharacterCombatTransform> transforms) =>
        JsonSerializer.Serialize(transforms);

    internal static string SerializeSourceOptionManifest(
        IReadOnlyList<string> sourceOptionIds)
    {
        var normalized = sourceOptionIds
            .Where(ValidateText)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length != sourceOptionIds.Count ||
            normalized.Length > MaximumSourceOptionCount)
        {
            ModLog.Warn("联机合并皮肤来源数量或格式无效，已对远端玩家回退为原皮。");
            return "[]";
        }

        var manifest = JsonSerializer.Serialize(normalized);
        if (manifest.Length > MaximumSourceOptionManifestLength)
        {
            ModLog.Warn("联机合并皮肤来源清单过大，已对远端玩家回退为原皮。");
            return "[]";
        }

        return manifest;
    }

    internal static bool TryParseSourceOptionManifest(
        string? manifest,
        out IReadOnlyList<string> sourceOptionIds)
    {
        sourceOptionIds = [];
        if (string.IsNullOrWhiteSpace(manifest) ||
            manifest.Length > MaximumSourceOptionManifestLength)
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<string[]>(manifest);
            if (parsed == null || parsed.Length > MaximumSourceOptionCount ||
                parsed.Any(sourceId => !ValidateText(sourceId)) ||
                parsed.Distinct(StringComparer.OrdinalIgnoreCase).Count() != parsed.Length)
            {
                return false;
            }

            sourceOptionIds = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

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
        if (!ShouldApplyRemoteAppearance())
        {
            return;
        }

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
            !TryParseSourceOptionManifest(
                message.SourceOptionManifest,
                out var advertisedSourceIds) ||
            !TryParseTransformManifest(
                message.TransformManifest,
                message.GroupId,
                out var transforms))
        {
            return;
        }

        var allowRemoteSkin = SkinService.ShouldReceiveMultiplayerSkinChanges();
        var effectiveOptionId = SkinCatalog.BaseOptionId;
        IReadOnlyDictionary<string, string> selectionOverrides;
        var availableSourceIds = allowRemoteSkin
            ? SkinService.GetAvailableCharacterSelectionSourceIds(
                message.GroupId,
                advertisedSourceIds)
            : [];
        bool selectionAvailable;
        var ownerAppearanceLoaded = false;
        if (!allowRemoteSkin || advertisedSourceIds.Count == 0)
        {
            selectionAvailable = SkinService.TryBuildSessionCharacterSelection(
                message.GroupId,
                SkinCatalog.BaseOptionId,
                out selectionOverrides);
        }
        else if (availableSourceIds.Count > 0)
        {
            selectionAvailable = SkinService.TryBuildSessionCharacterComposition(
                message.GroupId,
                availableSourceIds,
                out selectionOverrides,
                out effectiveOptionId);
            ownerAppearanceLoaded = selectionAvailable;
        }
        else
        {
            selectionOverrides = null!;
            selectionAvailable = false;
        }
        var fallbackKey = (message.PlayerNetId, message.GroupId);

        if (!selectionAvailable)
        {
            if (allowRemoteSkin && advertisedSourceIds.Count > 0)
            {
                var warningKey = $"{message.PlayerNetId}\n{message.GroupId}\n" +
                                 message.SourceOptionManifest;
                lock (Sync)
                {
                    if (MissingInstalledSkinWarnings.Add(warningKey))
                    {
                        ModLog.Info(
                            $"联机玩家 {message.PlayerNetId} 选择了本机未安装的皮肤 " +
                            $"{string.Join(", ", advertisedSourceIds)}；已使用原皮，不会下载远端资源。");
                    }
                }
            }

            string? localFallbackOption;
            lock (Sync)
            {
                LocalFallbackSelections.TryGetValue(fallbackKey, out localFallbackOption);
            }
            if (!string.IsNullOrWhiteSpace(localFallbackOption) &&
                SkinService.TryBuildSessionCharacterSelection(
                    message.GroupId,
                    localFallbackOption,
                    out selectionOverrides))
            {
                effectiveOptionId = localFallbackOption;
                selectionAvailable = true;
                ownerAppearanceLoaded = false;
            }

            // The sender owns a skin we do not have. Build an explicit per-player base selection
            // instead of leaking this machine's current skin for the same character onto them.
            if (!selectionAvailable && !SkinService.TryBuildSessionCharacterSelection(
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
            if (!selectionAvailable)
            {
                effectiveOptionId = SkinCatalog.BaseOptionId;
                ownerAppearanceLoaded = false;
            }
        }

        var appearanceChanged = false;
        lock (Sync)
        {
            if (AvailableSelections.TryGetValue(message.PlayerNetId, out var current) &&
                (!current.CharacterId.Equals(
                     message.CharacterId,
                     StringComparison.OrdinalIgnoreCase) ||
                 !current.GroupId.Equals(
                     message.GroupId,
                     StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var key in LocalFallbackTransforms.Keys
                             .Where(key => key.PlayerId == message.PlayerNetId)
                             .ToArray())
                {
                    LocalFallbackTransforms.Remove(key);
                }
            }
            if (ownerAppearanceLoaded)
            {
                LocalFallbackSelections.Remove(fallbackKey);
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
                                        message.SourceOptionManifest + "\n" +
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

    private static void RetryAdvertisementsWaitingForPlayerIdentity()
    {
        SkinChangerNetMessage[] pending;
        lock (Sync)
        {
            pending = AdvertisedSelections.Values
                .Where(message => message.PlayerNetId != _netService?.NetId &&
                                  (!AvailableSelections.TryGetValue(
                                       message.PlayerNetId,
                                       out var selection) ||
                                   !selection.CharacterId.Equals(
                                       message.CharacterId,
                                       StringComparison.OrdinalIgnoreCase) ||
                                   !selection.GroupId.Equals(
                                       message.GroupId,
                                       StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        }

        foreach (var message in pending)
        {
            TryMakeSelectionAvailable(message);
        }
    }

    internal static bool IsReadyGateActiveForOnlineCache() => false;

    private static void RememberLocalAdvertisement(bool baseOnly = false)
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
            OptionId = baseOnly
                ? SkinCatalog.BaseOptionId
                : SkinService.Config.GetSelection(group.Id)
        };
        message.SourceOptionManifest = baseOnly
            ? "[]"
            : SerializeSourceOptionManifest(
                SkinService.GetCharacterSelectionSourceIds(
                    message.GroupId,
                    message.OptionId));
        message.TransformManifest = SerializeTransformManifest(
            SkinService.GetSessionCharacterCombatTransforms(
                message.GroupId,
                message.OptionId));
        RememberAdvertisement(message);
    }

    internal static void RetryCachedSelection(
        SkinChangerNetMessage message,
        string sessionOptionId)
    {
        // Compatibility no-op for the retired OnlineSkinCache worker. Protocol 9 never starts
        // that worker and never accepts a downloaded session option.
    }

    internal static void OnLocalOnlineMetadataReady(string groupId, string optionId)
    {
        // Compatibility no-op for the retired OnlineSkinCache worker.
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
            AdvertisedSelections[message.PlayerNetId] = message;
        }
    }

    private static bool SameAdvertisedSelection(
        SkinChangerNetMessage left,
        SkinChangerNetMessage right) =>
        left.PlayerNetId == right.PlayerNetId &&
        string.Equals(left.CharacterId, right.CharacterId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.GroupId, right.GroupId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.OptionId, right.OptionId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            left.SourceOptionManifest,
            right.SourceOptionManifest,
            StringComparison.Ordinal);

    private static void SendLocalAdvertisement(bool force = false)
    {
        if (!force && !SkinService.ShouldSendMultiplayerSkinChanges())
        {
            return;
        }

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
            RelayFromHost(message, force: force);
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
        ulong? exceptPeerId = null,
        bool force = false)
    {
        if (!ShouldParticipate())
        {
            return;
        }

        if (!force && message.PlayerNetId == _netService?.NetId &&
            !SkinService.ShouldSendMultiplayerSkinChanges())
        {
            return;
        }

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
            foreach (var key in LocalFallbackSelections.Keys
                         .Where(key => key.PlayerId == peerId)
                         .ToArray())
            {
                LocalFallbackSelections.Remove(key);
            }
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

    private sealed class CombinedScope(IDisposable selectionScope, IDisposable resourceScope) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            resourceScope.Dispose();
            selectionScope.Dispose();
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
    [HarmonyPriority(Priority.First)]
    private static void Prefix(Creature __instance, out IDisposable? __state) =>
        // Keep both the per-player selection and its canonical scene/skeleton overlay active
        // for the complete CreateVisuals call. Selection-only scoping still resolved the local
        // player's globally mounted resources, especially when the remote player used base.
        __state = MultiplayerSkinSync.BeginCreatureRuntimeScope(__instance);

    [HarmonyPriority(Priority.Last)]
    private static void Postfix(
        Creature __instance,
        ref NCreatureVisuals? __result,
        IDisposable? __state)
    {
        if (__state != null)
        {
            // CharacterModel is shared by every player using that character.  Make the final
            // result owner-aware only after all CharacterModel postfixes have run, otherwise a
            // later global provider can overwrite this player's isolated selection.
            ContextualSkinControls.ReplaceCreatedCreatureVisuals(__instance, ref __result);
        }
    }

    private static Exception? Finalizer(Exception? __exception, IDisposable? __state)
    {
        __state?.Dispose();
        return __exception;
    }
}

// Creature.CreateVisuals covers the initial scene construction, but NCreature._Ready then
// attaches the scene and initializes its Spine/auxiliary nodes. Those nodes may resolve deferred
// binary resources or run a provider's selected visual finishing code. Keep the same owner scope
// active for the whole node initialization so a remote player's model cannot fall back to the
// local player's skin between CreateVisuals and _Ready.
[HarmonyPatch(typeof(NCreature), nameof(NCreature._Ready))]
internal static class MultiplayerCreatureReadyScopePatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(NCreature __instance, out IDisposable? __state) =>
        __state = __instance.Entity == null
            ? null
            : MultiplayerSkinSync.BeginCreatureRuntimeScope(__instance.Entity);

    [HarmonyPriority(Priority.Last)]
    private static void Postfix(NCreature __instance)
    {
        if (__instance.Entity != null)
        {
            // Provider _Ready presentation callbacks are isolated from their global Harmony
            // owner and replayed here while this creature's multiplayer selection scope is still
            // active. This prevents one player's late-attached actor from covering another's.
            CharacterAppearanceRuntime.ReplaySelectedCreatureNodeReady(__instance);
        }
    }

    private static Exception? Finalizer(Exception? __exception, IDisposable? __state)
    {
        __state?.Dispose();
        return __exception;
    }
}
