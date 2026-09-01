using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

/// <summary>
/// Rebuilds the two shop-only visual families after a hot selection: playable-character shop
/// poses and the merchant's body/hand presentation. The inventory model and purchased items stay
/// untouched.
/// </summary>
internal static class MerchantRuntimeAppearance
{
    internal const string GroupId = "merchant";
    internal const string FakeMerchantGroupId = "fake_merchant_monster";
    private const string MerchantRoomScenePath = "res://scenes/rooms/merchant_room.tscn";
    private const string FakeMerchantScenePath = "res://scenes/events/custom/fake_merchant.tscn";
    private const string MerchantInventoryScenePath = "res://scenes/merchant/merchant_inventory.tscn";
    private const string MerchantPreviewRootMeta = "skin_changer_merchant_preview_root";
    private const string MerchantPreviewInputProxyName = "SkinChangerMerchantInputProxy";
    private const string MerchantPreviewFocusedMeta = "skin_changer_merchant_preview_focused";
    private const string PlayerBasePositionMeta = "skin_changer_shop_player_base_position";
    private const string PlayerBaseScaleMeta = "skin_changer_shop_player_base_scale";
    private const string ProviderRootBasePositionMeta = "skin_changer_shop_provider_root_position";
    private const string ProviderRootBaseScaleMeta = "skin_changer_shop_provider_root_scale";

    private static readonly FieldInfo? MerchantButtonField =
        AccessTools.Field(typeof(NMerchantRoom), "<MerchantButton>k__BackingField");
    private static readonly FieldInfo? FakeMerchantButtonField =
        AccessTools.Field(typeof(NFakeMerchant), "<MerchantButton>k__BackingField");
    private static readonly MethodInfo? FakeMerchantOpenedMethod =
        AccessTools.Method(typeof(NFakeMerchant), "OnMerchantOpened");
    private static readonly FieldInfo? MerchantHandField =
        AccessTools.Field(typeof(NMerchantInventory), "<MerchantHand>k__BackingField");
    private static readonly FieldInfo? MerchantRoomInventoryField =
        AccessTools.Field(typeof(NMerchantRoom), "<Inventory>k__BackingField");
    private static readonly FieldInfo? MerchantRoomProceedButtonField =
        AccessTools.Field(typeof(NMerchantRoom), "_proceedButton");
    private static readonly FieldInfo? FakeMerchantInventoryField =
        AccessTools.Field(typeof(NFakeMerchant), "<Inventory>k__BackingField");
    private static readonly FieldInfo? FakeMerchantProceedButtonField =
        AccessTools.Field(typeof(NFakeMerchant), "_proceedButton");
    private static readonly MethodInfo? FakeMerchantOpenInventoryMethod =
        AccessTools.Method(typeof(NFakeMerchant), "OpenInventory");
    private static readonly FieldInfo? MerchantRoomModelField =
        AccessTools.Field(typeof(NMerchantRoom), "<Room>k__BackingField");
    private static readonly FieldInfo? MerchantRoomPlayersField =
        AccessTools.Field(typeof(NMerchantRoom), "_players");
    private static readonly FieldInfo? MerchantRoomDialogueField =
        AccessTools.Field(typeof(NMerchantRoom), "_dialogue");
    private static readonly FieldInfo? MerchantSkeletonField =
        AccessTools.Field(typeof(NMerchantButton), "_merchantSkeleton");
    private static readonly MethodInfo? MegaSkeletonSetSkinByNameMethod =
        AccessTools.Method(typeof(MegaSkeleton), nameof(MegaSkeleton.SetSkinByName));
    private static readonly List<WeakReference<Node>> ReplayedInventoryAdditions = [];
    private static readonly Dictionary<ulong, List<WeakReference<Node2D>>> ShopProviderRoots = [];

    internal static NMerchantCharacter? GetLocalPlayerVisual()
    {
        var room = NMerchantRoom.Instance;
        return room is { PlayerVisuals.Count: > 0 } ? room.PlayerVisuals[0] : null;
    }

    internal static string? GetSelectedLocalCharacterProvider()
    {
        var player = CharacterAppearanceRuntime.GetLocalPlayer();
        if (player == null)
        {
            return null;
        }

        var group = ContextualSkinControls.FindGroup(
            player.Character.Id.Entry,
            player.Character.GetType().Name);
        return group == null
            ? null
            : SkinService.GetSelectedFullRuntimeProvider(group.Id);
    }

    internal static string? BeginSelectedMerchantNodeReady(Node node)
    {
        var groupId = ResolveMerchantGroupId(node);
        var providerId = groupId == null
            ? null
            : SkinService.GetSelectedFullRuntimeProvider(groupId);
        if (providerId == null)
        {
            return null;
        }

        TrackInventoryProviderAdditions(
            node,
            ManagedSkinModLoader.ReplaySelectedNodeReadyPrefixes(providerId, node));
        return providerId;
    }

    internal static void CompleteSelectedMerchantNodeReady(Node node, string? providerId)
    {
        if (providerId == null || !GodotObject.IsInstanceValid(node))
        {
            return;
        }

        var target = node switch
        {
            NMerchantButton => MerchantProviderReadyTarget.Button,
            NMerchantHand => MerchantProviderReadyTarget.Hand,
            _ => MerchantProviderReadyTarget.Inventory
        };
        if (MerchantProviderReadyPolicy.ResolvePostfixTiming(target) ==
            MerchantProviderPostfixTiming.NextFrameThenSpineReady)
        {
            // A provider prefix can replace skeleton_data_res while the outgoing skeleton still
            // reports a ready animation state. Defer one frame before resolving MegaSprite, then
            // use the game's own readiness gate so the provider registers the final skeleton.
            Callable.From(() => CompleteSelectedMerchantSpineNodeReady(node, providerId))
                .CallDeferred();
            return;
        }

        CompleteSelectedMerchantNodeReadyCore(node, providerId);
    }

    private static void CompleteSelectedMerchantSpineNodeReady(Node node, string providerId)
    {
        if (!IsSelectedProviderForNode(node, providerId))
        {
            return;
        }

        try
        {
            var spineNode = node switch
            {
                NMerchantButton button => button.GetNodeOrNull<Node>("%MerchantVisual"),
                NMerchantHand hand => hand.GetParent<Node2D>(),
                _ => null
            };
            if (spineNode == null || !GodotObject.IsInstanceValid(spineNode))
            {
                CompleteSelectedMerchantNodeReadyCore(node, providerId);
                return;
            }

            var sprite = new MegaSprite(spineNode);
            node.RunWhenSpineReady(sprite, _ =>
            {
                if (IsSelectedProviderForNode(node, providerId))
                {
                    CompleteSelectedMerchantNodeReadyCore(node, providerId);
                }
            });
        }
        catch (Exception exception)
        {
            ModLog.Warn(
                $"等待商人外观骨骼就绪失败，改为完成原作者初始化：" +
                exception.GetBaseException().Message);
            if (IsSelectedProviderForNode(node, providerId))
            {
                CompleteSelectedMerchantNodeReadyCore(node, providerId);
            }
        }
    }

    private static bool IsSelectedProviderForNode(Node node, string providerId)
    {
        if (!GodotObject.IsInstanceValid(node))
        {
            return false;
        }

        var groupId = ResolveMerchantGroupId(node);
        return groupId != null &&
               string.Equals(
                   SkinService.GetSelectedFullRuntimeProvider(groupId),
                   providerId,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static void CompleteSelectedMerchantNodeReadyCore(Node node, string providerId)
    {
        TrackInventoryProviderAdditions(
            node,
            ManagedSkinModLoader.ReplaySelectedNodeReadyPostfixes(providerId, node));
        if (node is NMerchantInventory)
        {
            MakeProviderInventoryVisualsPassThrough();
        }
    }

    private static string? ResolveMerchantGroupId(Node node)
    {
        for (Node? current = node; current != null; current = current.GetParent())
        {
            if (current is NFakeMerchant or NFakeMerchantInventory)
            {
                return FakeMerchantGroupId;
            }

            if (current is NMerchantRoom)
            {
                return GroupId;
            }
        }

        return null;
    }

    private static void TrackInventoryProviderAdditions(
        Node owner,
        IEnumerable<Node> addedRoots)
    {
        if (owner is not NMerchantInventory)
        {
            return;
        }

        var existingIds = ReplayedInventoryAdditions
            .Select(reference => reference.TryGetTarget(out var node) &&
                                 GodotObject.IsInstanceValid(node)
                ? node.GetInstanceId()
                : 0UL)
            .ToHashSet();
        foreach (var addedRoot in addedRoots.Where(GodotObject.IsInstanceValid))
        {
            if (existingIds.Add(addedRoot.GetInstanceId()))
            {
                ReplayedInventoryAdditions.Add(new WeakReference<Node>(addedRoot));
            }
        }
    }

    internal static CharacterCombatTransform GetLocalPlayerTransform(string groupId)
    {
        return SkinService.GetCharacterCombatTransform(
            GetLocalPlayerTransformKey(groupId),
            SkinService.Config.GetSelection(groupId));
    }

    internal static CharacterCombatTransform SetLocalPlayerTransform(
        string groupId,
        CharacterCombatTransform value,
        bool save)
    {
        var optionId = SkinService.Config.GetSelection(groupId);
        var normalized = SkinService.SetCharacterCombatTransform(
            GetLocalPlayerTransformKey(groupId),
            optionId,
            value,
            save);
        MultiplayerSkinSync.OnLocalTransformChanged(groupId);
        return normalized;
    }

    internal static void ApplyLocalPlayerTransform(
        NMerchantCharacter visual,
        string groupId,
        CharacterCombatTransform? value = null)
    {
        CaptureLocalPlayerBaseline(visual);
        var basePosition = visual.GetMeta(PlayerBasePositionMeta, visual.Position).AsVector2();
        var baseScale = visual.GetMeta(PlayerBaseScaleMeta, visual.Scale).AsVector2();
        var transform = value ?? GetLocalPlayerTransform(groupId);
        visual.Position = basePosition + new Vector2(transform.OffsetX, transform.OffsetY);
        visual.Scale = baseScale * transform.Scale;
        ApplyProviderRootsTransform(visual, transform);
    }

    internal static void TrackProviderRoots(
        NMerchantCharacter visual,
        IEnumerable<Node> addedRoots)
    {
        if (!GodotObject.IsInstanceValid(visual))
        {
            return;
        }

        var candidates = addedRoots
            .OfType<Node2D>()
            .Where(node => GodotObject.IsInstanceValid(node) && !ReferenceEquals(node, visual))
            .ToArray();
        var sameParentCandidates = candidates
            .Where(node => ReferenceEquals(node.GetParent(), visual.GetParent()))
            .ToArray();
        var candidatePool = sameParentCandidates.Length > 0
            ? sameParentCandidates
            : candidates;
        var nearestDistance = candidatePool.Length == 0
            ? float.PositiveInfinity
            : candidatePool.Min(node => node.GlobalPosition.DistanceSquaredTo(visual.GlobalPosition));
        var selected = candidatePool
            .Where(node => nearestDistance < float.PositiveInfinity &&
                          (candidatePool.Length == 1 ||
                           node.GlobalPosition.DistanceSquaredTo(visual.GlobalPosition) <=
                           nearestDistance + 16f))
            .Select(node => new WeakReference<Node2D>(node))
            .ToList();
        var visualId = visual.GetInstanceId();
        if (selected.Count == 0)
        {
            ShopProviderRoots.Remove(visualId);
        }
        else
        {
            ShopProviderRoots[visualId] = selected;
        }
    }

    internal static void PrepareMerchantSelectionChange()
    {
        var room = NMerchantRoom.Instance;
        var providerId = SkinService.GetSelectedFullRuntimeProvider(GroupId);
        if (room?.Inventory != null && providerId != null)
        {
            // A provider can remain active because its fake-merchant skin is still selected.
            // Restore the real inventory explicitly before changing only the real merchant so
            // a prefix-added leg or other inventory visual cannot survive into the next skin.
            ManagedSkinModLoader.RestoreSelectedNodeReadyBehavior(providerId, room.Inventory);
        }

        foreach (var reference in ReplayedInventoryAdditions.ToArray())
        {
            if (!reference.TryGetTarget(out var node) || !GodotObject.IsInstanceValid(node))
            {
                continue;
            }

            node.GetParent()?.RemoveChild(node);
            node.QueueFree();
        }

        ReplayedInventoryAdditions.Clear();
    }

    internal static bool TryCreateCurrentMerchantRoom(
        NMerchantRoom preloadCreatedRoom,
        out NMerchantRoom? currentRoom,
        out string? error)
    {
        currentRoom = null;
        error = null;
        if (MerchantRoomModelField == null ||
            MerchantRoomPlayersField == null ||
            MerchantRoomDialogueField == null)
        {
            error = "merchant room state fields unavailable";
            return false;
        }

        try
        {
            // NMerchantRoom.Create normally instantiates PreloadManager's startup-cached scene.
            // A merchant selection changed later in the same game process cannot invalidate that
            // cache, so save/reload would recreate the startup skin while replaying the current
            // provider's code. Build from SkinChanger's selection-keyed runtime scene instead.
            // InstantiateRuntimeScene keeps the selected provider pack mounted while Godot resolves
            // lazy external resources (especially ATA/Merchant2Cute Spine data). If a provider
            // does not ship a replacement room scene, retain the base-game fallback.
            NMerchantRoom replacement;
            var replacementTransferred = false;
            try
            {
                replacement = SkinService.InstantiateRuntimeScene<NMerchantRoom>(
                    GroupId,
                    MerchantRoomScenePath);
            }
            catch (Exception runtimeException)
            {
                ModLog.Warn("当前商人没有独立房间场景，使用游戏场景兜底：" +
                            runtimeException.GetBaseException().Message);
                replacement = LoadRuntimeOrBaseScene(MerchantRoomScenePath)
                    .Instantiate<NMerchantRoom>(PackedScene.GenEditState.Disabled);
            }
            try
            {
                MerchantRoomModelField.SetValue(
                    replacement,
                    MerchantRoomModelField.GetValue(preloadCreatedRoom));
                MerchantRoomDialogueField.SetValue(
                    replacement,
                    MerchantRoomDialogueField.GetValue(preloadCreatedRoom));

                var sourcePlayers = MerchantRoomPlayersField.GetValue(preloadCreatedRoom)
                                        as IEnumerable<Player> ??
                                    throw new InvalidOperationException(
                                        "预加载商店房间缺少玩家列表");
                var targetPlayers = MerchantRoomPlayersField.GetValue(replacement)
                                        as List<Player> ??
                                    throw new InvalidOperationException(
                                        "当前商店房间缺少玩家列表");
                targetPlayers.AddRange(sourcePlayers);

                currentRoom = replacement;
                replacementTransferred = true;
                return true;
            }
            finally
            {
                if (!replacementTransferred && GodotObject.IsInstanceValid(replacement))
                {
                    replacement.Free();
                }
            }
        }
        catch (Exception exception)
        {
            error = exception.GetBaseException().Message;
            ModLog.Error("按当前皮肤创建商店房间失败：" + exception);
            return false;
        }
    }

    internal static bool IsMerchantPreviewRoot(Node node) =>
        GodotObject.IsInstanceValid(node) &&
        node.HasMeta(MerchantPreviewRootMeta) &&
        node.GetMeta(MerchantPreviewRootMeta).AsBool();

    private static void InstallMerchantPreviewInputProxy(NMerchantButton button)
    {
        if (button.GetNodeOrNull<Control>(MerchantPreviewInputProxyName) != null)
        {
            return;
        }

        // Do not Harmony-patch NMerchantButton.RefreshFocus for the catalogue. Patching that live
        // method during SkinChanger startup makes the JIT bake the then-unpatched SetSkinByName
        // body into the shop's focus path; providers activated later can no longer remap
        // "outline" there. A transparent child owns catalogue input instead, leaving every live
        // merchant method untouched and letting the active provider receive skin changes through
        // reflection (which cannot inline past Harmony's current entry point).
        button.MouseFilter = Control.MouseFilterEnum.Ignore;
        button.FocusMode = Control.FocusModeEnum.None;
        button.SetMeta(MerchantPreviewFocusedMeta, false);

        var proxy = new Control
        {
            Name = MerchantPreviewInputProxyName,
            MouseFilter = Control.MouseFilterEnum.Stop,
            FocusMode = Control.FocusModeEnum.All,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            ZIndex = 100
        };
        button.AddChild(proxy);
        proxy.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        proxy.MoveToFront();

        var focusState = MerchantPreviewFocusState.None;
        var pressed = false;
        void Transition(MerchantPreviewFocusEvent focusEvent)
        {
            var wasFocused = focusState.IsFocused;
            focusState = MerchantPreviewFocusPolicy.Resolve(focusState, focusEvent);
            if (wasFocused == focusState.IsFocused)
            {
                return;
            }

            button.SetMeta(MerchantPreviewFocusedMeta, focusState.IsFocused);
            ApplyMerchantPreviewFocus(button);
        }

        proxy.MouseEntered += () => Transition(MerchantPreviewFocusEvent.MouseEntered);
        proxy.MouseExited += () =>
        {
            pressed = false;
            Transition(MerchantPreviewFocusEvent.MouseExited);
        };
        proxy.FocusEntered += () => Transition(MerchantPreviewFocusEvent.ControllerFocused);
        proxy.FocusExited += () => Transition(MerchantPreviewFocusEvent.ControllerUnfocused);
        proxy.GuiInput += inputEvent =>
        {
            if (inputEvent is InputEventMouseButton { ButtonIndex: MouseButton.Left } mouseButton)
            {
                if (mouseButton.Pressed)
                {
                    pressed = true;
                }
                else if (pressed)
                {
                    pressed = false;
                    button.ForceClick();
                }

                proxy.AcceptEvent();
                return;
            }

            if (inputEvent.IsActionPressed(MegaInput.select))
            {
                pressed = true;
                proxy.AcceptEvent();
            }
            else if (inputEvent.IsActionReleased(MegaInput.select) && pressed)
            {
                pressed = false;
                button.ForceClick();
                proxy.AcceptEvent();
            }
        };
    }

    private static void ApplyMerchantPreviewFocus(NMerchantButton button)
    {
        if (!GodotObject.IsInstanceValid(button))
        {
            return;
        }

        var skeleton = MerchantSkeletonField?.GetValue(button) as MegaSkeleton;
        if (skeleton == null)
        {
            var visualNode = button.GetNodeOrNull<Node>("%MerchantVisual");
            if (visualNode != null)
            {
                var sprite = new MegaSprite(visualNode);
                button.RunWhenSpineReady(sprite, _ => ApplyMerchantPreviewFocus(button));
            }

            return;
        }

        var isFocused = button.HasMeta(MerchantPreviewFocusedMeta) &&
                        button.GetMeta(MerchantPreviewFocusedMeta).AsBool();
        try
        {
            if (MegaSkeletonSetSkinByNameMethod == null)
            {
                throw new MissingMethodException(
                    typeof(MegaSkeleton).FullName,
                    nameof(MegaSkeleton.SetSkinByName));
            }

            MegaSkeletonSetSkinByNameMethod.Invoke(
                skeleton,
                [isFocused ? "outline" : "default"]);
            skeleton.SetSlotsToSetupPose();
        }
        catch (Exception exception)
        {
            ModLog.Warn("应用图鉴商人悬浮外观失败：" + exception.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Initializes the local visual part of the game's native merchant room for a catalogue
    /// preview. The real room's _Ready also touches the map and run singletons, which do not exist
    /// while the compendium is open, so those two global calls are intentionally omitted. The
    /// button, inventory and open/close signal path are otherwise the same native nodes and methods
    /// used by a live shop.
    /// </summary>
    internal static NBackButton? PrepareMerchantPreviewInteraction(
        NMerchantRoom preview,
        Action<bool>? inventoryVisibilityChanged = null)
    {
        try
        {
            if (MerchantButtonField == null ||
                MerchantRoomInventoryField == null ||
                MerchantRoomProceedButtonField == null)
            {
                ModLog.Warn("商人预览缺少原生交互字段，保留按钮视觉但不连接打开逻辑。");
                return null;
            }

            var button = preview.GetNodeOrNull<NMerchantButton>("%MerchantButton") ??
                         preview.FindChild("MerchantButton", recursive: true, owned: false) as NMerchantButton;
            var inventory = preview.GetNodeOrNull<NMerchantInventory>("%Inventory") ??
                            preview.FindChild("Inventory", recursive: true, owned: false) as NMerchantInventory;
            var proceed = preview.GetNodeOrNull<NProceedButton>("%ProceedButton") ??
                          preview.FindChild("ProceedButton", recursive: true, owned: false) as NProceedButton;
            if (button == null || inventory == null || proceed == null)
            {
                ModLog.Warn("商人预览场景缺少 MerchantButton/Inventory/ProceedButton，跳过原生交互连接。");
                return null;
            }

            var previewPlayer = CreatePreviewPlayer("SkinChangerMerchantPreview");
            previewPlayer.Gold = 9999;
            var model = MerchantInventory.CreateForNormalMerchant(previewPlayer);

            MerchantRoomInventoryField.SetValue(preview, inventory);
            MerchantRoomProceedButtonField.SetValue(preview, proceed);
            MerchantButtonField.SetValue(preview, button);
            button.IsLocalPlayerDead = false;
            button.PlayerDeadLines = MerchantRoom.Dialogue.PlayerDeadLines;
            InstallMerchantPreviewInputProxy(button);
            inventory.MouseFilter = Control.MouseFilterEnum.Ignore;
            inventory.Initialize(model, MerchantRoom.Dialogue);
            button.Connect(
                NMerchantButton.SignalName.MerchantOpened,
                Callable.From<NMerchantButton>(_ =>
                {
                    preview.OpenInventory();
                    inventoryVisibilityChanged?.Invoke(inventory.IsOpen);
                }));
            inventory.Connect(
                NMerchantInventory.SignalName.InventoryClosed,
                Callable.From(() => inventoryVisibilityChanged?.Invoke(false)));
            // A catalogue is not a room and cannot travel to the map. Keep the native node in the
            // hierarchy (so the scene layout remains identical), but hide the native proceed
            // control instead of exposing its uninitialized scene placeholder ("Tally-ho").
            proceed.Visible = false;
            ModLog.Info("商人预览已连接游戏原生 MerchantOpened → OpenInventory 交互路径。");
            return inventory.GetNodeOrNull<NBackButton>("%BackButton");
        }
        catch (Exception exception)
        {
            ModLog.Warn("连接商人预览原生交互失败：" + exception.GetBaseException().Message);
            return null;
        }
    }

    internal static NBackButton? PrepareFakeMerchantPreviewInteraction(
        NFakeMerchant preview,
        Action<bool>? inventoryVisibilityChanged = null)
    {
        try
        {
            if (FakeMerchantInventoryField == null ||
                FakeMerchantProceedButtonField == null ||
                FakeMerchantOpenInventoryMethod == null)
            {
                ModLog.Warn("假商人预览缺少原生交互字段，保留按钮视觉但不连接打开逻辑。");
                return null;
            }

            var button = preview.GetNodeOrNull<NMerchantButton>("%MerchantButton") ??
                         preview.FindChild("MerchantButton", recursive: true, owned: false) as NMerchantButton;
            var inventory = preview.GetNodeOrNull<NMerchantInventory>("%Inventory") ??
                            preview.FindChild("Inventory", recursive: true, owned: false) as NMerchantInventory;
            var proceed = preview.GetNodeOrNull<NProceedButton>("%ProceedButton") ??
                          preview.FindChild("ProceedButton", recursive: true, owned: false) as NProceedButton;
            if (button == null || inventory == null || proceed == null)
            {
                ModLog.Warn("假商人预览场景缺少 MerchantButton/Inventory/ProceedButton，跳过原生交互连接。");
                return null;
            }

            var previewPlayer = CreatePreviewPlayer("SkinChangerFakeMerchantPreview");
            previewPlayer.Gold = 9999;
            var model = new MerchantInventory(previewPlayer);
            foreach (var relic in ModelDb.AllRelics
                         .OrderBy(relic => relic.Id.Entry, StringComparer.OrdinalIgnoreCase)
                         .Take(6))
            {
                model.AddRelicEntry(new MerchantRelicEntry(relic.ToMutable(), previewPlayer));
            }

            FakeMerchantInventoryField.SetValue(preview, inventory);
            FakeMerchantProceedButtonField.SetValue(preview, proceed);
            AccessTools.Field(typeof(NFakeMerchant), "<MerchantButton>k__BackingField")?.SetValue(
                preview,
                button);
            InstallMerchantPreviewInputProxy(button);
            inventory.MouseFilter = Control.MouseFilterEnum.Ignore;
            inventory.Initialize(model, FakeMerchant.Dialogue);
            button.Connect(
                NMerchantButton.SignalName.MerchantOpened,
                Callable.From<NMerchantButton>(_ =>
                {
                    FakeMerchantOpenInventoryMethod.Invoke(preview, null);
                    inventoryVisibilityChanged?.Invoke(inventory.IsOpen);
                }));
            inventory.Connect(
                NMerchantInventory.SignalName.InventoryClosed,
                Callable.From(() => inventoryVisibilityChanged?.Invoke(false)));
            proceed.Visible = false;
            ModLog.Info("假商人预览已连接游戏原生 MerchantOpened → OpenInventory 交互路径。");
            return inventory.GetNodeOrNull<NBackButton>("%BackButton");
        }
        catch (Exception exception)
        {
            ModLog.Warn("连接假商人预览原生交互失败：" + exception.GetBaseException().Message);
            return null;
        }
    }

    private static Player CreatePreviewPlayer(string seed)
    {
        // Use the local net id when one is available so any native ownership checks see the same
        // player as the rest of the UI.  The fallback is only for the title screen before Steam
        // has assigned a local id.
        var netId = LocalContext.NetId ?? 1UL;
        var player = Player.CreateForNewRun(
            ModelDb.Character<Ironclad>(),
            UnlockState.all,
            netId);
        _ = RunState.CreateForTest([player], seed: seed);
        return player;
    }

    internal static bool TryRefreshMerchant(out string? error)
    {
        error = null;
        var room = NMerchantRoom.Instance;
        if (room == null || !GodotObject.IsInstanceValid(room))
        {
            error = "merchant room unavailable";
            return false;
        }

        if (MerchantButtonField == null || MerchantHandField == null)
        {
            error = "merchant visual fields unavailable";
            return false;
        }

        NMerchantButton? newButton = null;
        NMerchantInventory? inventoryTemplate = null;
        Node2D? newHandContainer = null;
        NMerchantButton? previousButton = null;
        Node2D? previousHandContainer = null;
        NMerchantHand? previousHand = null;
        var buttonSwapped = false;
        var handSwapped = false;
        try
        {
            newButton = InstantiateMerchantButton();
            try
            {
                // Inventory providers can replace the hand scene or its child Spine resources.
                // Instantiate while the selected merchant overlay is mounted so a hot swap never
                // binds the previous merchant's cached hand/skeleton.
                inventoryTemplate = SkinService.InstantiateRuntimeScene<NMerchantInventory>(
                    GroupId,
                    MerchantInventoryScenePath);
            }
            catch (Exception runtimeException)
            {
                ModLog.Warn("当前商人没有独立库存场景，使用游戏场景兜底：" +
                            runtimeException.GetBaseException().Message);
                inventoryTemplate = LoadRuntimeOrBaseScene(MerchantInventoryScenePath)
                    .Instantiate<NMerchantInventory>(PackedScene.GenEditState.Disabled);
            }
            newHandContainer = inventoryTemplate.GetNodeOrNull<Node2D>("MerchantHandContainer") ??
                               FindMerchantHand(inventoryTemplate)?.GetParent() as Node2D ??
                               throw new InvalidOperationException("商店库存场景缺少 MerchantHandContainer 节点");
            inventoryTemplate.RemoveChild(newHandContainer);

            ReplaceMerchantButton(room, newButton, out previousButton);
            buttonSwapped = true;
            ReplaceMerchantHand(room.Inventory, newHandContainer, out previousHandContainer, out previousHand);
            handSwapped = true;

            var providerId = SkinService.GetSelectedFullRuntimeProvider(GroupId);
            if (providerId != null)
            {
                // The replacement button and hand have already passed through the split native
                // _Ready bridge while being attached. The inventory itself is retained so bought
                // items and open state survive; replay only its selected provider callbacks here.
                foreach (var addedNode in ManagedSkinModLoader.ReplaySelectedNodeReadyBehavior(
                             providerId,
                             room.Inventory,
                             includePrefixes: true))
                {
                    ReplayedInventoryAdditions.Add(new WeakReference<Node>(addedNode));
                }

                // A merchant skin is visual-only. Some providers add a full-size Control to the
                // inventory root; leaving its default MouseFilter=Stop makes it sit above the
                // game's BackButton and swallow the click after a hot swap. Keep those provider
                // visuals transparent to input while preserving the game's own controls.
                MakeProviderInventoryVisualsPassThrough();
            }

            // Commit only after the complete replacement and provider replay succeeded. Keeping
            // the old nodes alive until this point makes a malformed skin recoverable instead of
            // leaving NMerchantRoom/NMerchantInventory pointing at half-initialized nodes.
            previousButton.GetParent()?.RemoveChild(previousButton);
            previousButton.QueueFree();
            previousHandContainer.GetParent()?.RemoveChild(previousHandContainer);
            previousHandContainer.QueueFree();
            newButton = null;
            newHandContainer = null;
            buttonSwapped = false;
            handSwapped = false;

            return true;
        }
        catch (Exception exception)
        {
            try
            {
                // Remove the failed replacements before restoring the original names. Godot node
                // names must be unique among siblings; restoring while both copies are still
                // parented can silently rename the original and break later % lookups.
                if (handSwapped && newHandContainer != null && GodotObject.IsInstanceValid(newHandContainer))
                {
                    newHandContainer.GetParent()?.RemoveChild(newHandContainer);
                    newHandContainer.Free();
                    newHandContainer = null;
                }

                if (buttonSwapped && newButton != null && GodotObject.IsInstanceValid(newButton))
                {
                    newButton.GetParent()?.RemoveChild(newButton);
                    newButton.Free();
                    newButton = null;
                }

                if (buttonSwapped && previousButton != null && GodotObject.IsInstanceValid(previousButton))
                {
                    MerchantButtonField.SetValue(room, previousButton);
                    previousButton.Name = previousButton.Name.ToString().Replace(
                        "__SkinChangerPrevious",
                        string.Empty,
                        StringComparison.Ordinal);
                }

                if (handSwapped && previousHand != null && GodotObject.IsInstanceValid(previousHand))
                {
                    MerchantHandField.SetValue(room.Inventory, previousHand);
                    if (previousHandContainer != null && GodotObject.IsInstanceValid(previousHandContainer))
                    {
                        previousHandContainer.Name = previousHandContainer.Name.ToString().Replace(
                            "__SkinChangerPrevious",
                            string.Empty,
                            StringComparison.Ordinal);
                    }
                }
            }
            catch (Exception rollbackException)
            {
                ModLog.Error("回滚商人外观失败：" + rollbackException);
            }

            error = exception.GetBaseException().Message;
            ModLog.Error("刷新商人外观失败：" + exception);
            return false;
        }
        finally
        {
            if (newButton != null && GodotObject.IsInstanceValid(newButton))
            {
                newButton.Free();
            }

            if (newHandContainer != null && GodotObject.IsInstanceValid(newHandContainer))
            {
                newHandContainer.Free();
            }

            if (inventoryTemplate != null && GodotObject.IsInstanceValid(inventoryTemplate))
            {
                inventoryTemplate.Free();
            }
        }
    }

    internal static bool TryRefreshFakeMerchant(
        NFakeMerchant fakeMerchant,
        out string? error)
    {
        error = null;
        if (!GodotObject.IsInstanceValid(fakeMerchant) ||
            fakeMerchant.MerchantButton == null ||
            FakeMerchantButtonField == null ||
            FakeMerchantOpenedMethod == null)
        {
            error = "fake merchant visual fields unavailable";
            return false;
        }

        NMerchantButton? replacement = null;
        NMerchantButton? previous = null;
        var swapped = false;
        var previousName = string.Empty;
        try
        {
            previous = fakeMerchant.MerchantButton;
            previousName = previous.Name;
            replacement = InstantiateMerchantButton(FakeMerchantGroupId);
            ReplaceMerchantButtonCore(
                previous,
                replacement,
                fakeMerchant,
                FakeMerchantButtonField,
                button => FakeMerchantOpenedMethod.Invoke(fakeMerchant, [button]));
            swapped = true;

            var providerId = SkinService.GetSelectedFullRuntimeProvider(FakeMerchantGroupId);

            // The fake merchant event keeps its inventory and dialogue objects; only the
            // merchant button/visual is replaced. This avoids re-running event initialization
            // and preserves the event's own opened/closed callbacks.
            previous.GetParent()?.RemoveChild(previous);
            previous.QueueFree();

            ManagedSkinModLoader.RestoreUnselectedNodeReadyBehaviors(
                fakeMerchant,
                providerId == null ? [] : [providerId]);
            ManagedSkinModLoader.RestoreUnselectedNodeReadyBehaviors(
                fakeMerchant.Inventory,
                providerId == null ? [] : [providerId]);
            ManagedSkinModLoader.RestoreUnselectedNodeReadyBehaviors(
                fakeMerchant.Inventory.MerchantHand,
                providerId == null ? [] : [providerId]);
            if (providerId != null)
            {
                ManagedSkinModLoader.ReplaySelectedRoomReadyBehaviors(fakeMerchant, providerId);
                _ = ManagedSkinModLoader.ReplaySelectedNodeReadyBehavior(
                    providerId,
                    fakeMerchant.Inventory,
                    includePrefixes: true);
                _ = ManagedSkinModLoader.ReplaySelectedNodeReadyBehavior(
                    providerId,
                    fakeMerchant.Inventory.MerchantHand);
            }

            replacement = null;
            return true;
        }
        catch (Exception exception)
        {
            if (replacement != null && GodotObject.IsInstanceValid(replacement))
            {
                replacement.GetParent()?.RemoveChild(replacement);
            }

            if (swapped && previous != null && GodotObject.IsInstanceValid(previous))
            {
                FakeMerchantButtonField.SetValue(fakeMerchant, previous);
                previous.Name = previousName;
            }

            error = exception.GetBaseException().Message;
            ModLog.Error("刷新假商人外观失败：" + exception);
            return false;
        }
        finally
        {
            if (replacement != null && GodotObject.IsInstanceValid(replacement))
            {
                replacement.GetParent()?.RemoveChild(replacement);
                replacement.Free();
            }
        }
    }

    internal static bool TryRefreshLocalPlayer(
        Player player,
        string groupId,
        out NMerchantCharacter? refreshedVisual,
        out string? error)
    {
        refreshedVisual = null;
        error = null;
        var room = NMerchantRoom.Instance;
        if (room == null || !GodotObject.IsInstanceValid(room))
        {
            error = "merchant room unavailable";
            return false;
        }

        if (room.PlayerVisuals is not List<NMerchantCharacter> visuals || visuals.Count == 0)
        {
            error = "merchant player visual unavailable";
            return false;
        }

        NMerchantCharacter? replacement = null;
        NMerchantCharacter? previous = null;
        Node? parent = null;
        var previousName = string.Empty;
        var previousIndex = -1;
        var listSwapped = false;
        var replacementAdded = false;
        var replayedRoots = new List<Node>();
        try
        {
            previous = visuals[0];
            parent = previous.GetParent() ??
                     throw new InvalidOperationException("merchant player visual has no parent");
            previousIndex = previous.GetIndex();
            previousName = previous.Name.ToString();
            replacement = SkinService
                .GetOrLoadRuntimeScene(groupId, player.Character.MerchantAnimPath)
                .Instantiate<NMerchantCharacter>(PackedScene.GenEditState.Disabled);

            // NMerchantRoom.AfterRoomIsLoaded always assigns the local player's slot to (0, 0).
            // Do not inherit a previous skin's already-adjusted root position: that turns a
            // provider-specific offset into the next skin's permanent anchor.
            replacement.Position = Vector2.Zero;
            replacement.Visible = previous.Visible;
            replacement.Modulate = previous.Modulate;
            replacement.SelfModulate = previous.SelfModulate;
            replacement.ZIndex = previous.ZIndex;
            replacement.ZAsRelative = previous.ZAsRelative;

            // Make the replacement visible through NMerchantRoom.PlayerVisuals while AddChild
            // invokes its _Ready callbacks. Character providers commonly resolve their creature
            // from that list during _Ready.
            previous.Name = previousName + "__SkinChangerPrevious";
            replacement.Name = previousName;
            visuals[0] = replacement;
            listSwapped = true;
            parent.AddChild(replacement);
            replacementAdded = true;
            parent.MoveChild(replacement, previousIndex);

            // Complete character skins can attach a separate shop presentation from an
            // NMerchantRoom._Ready postfix. That callback already ran when the room was first
            // created, so replay it after a hot swap as well; the loader tracks its added nodes
            // and visibility changes and restores them when the provider is left.
            var providerId = SkinService.GetSelectedFullRuntimeProvider(groupId);
            if (providerId != null)
            {
                foreach (var replay in ManagedSkinModLoader.ReplaySelectedRoomReadyBehaviors(room, providerId))
                {
                    replayedRoots.AddRange(replay.AddedRoots);
                    TrackProviderRoots(replacement, replay.AddedRoots);
                }
            }

            // Match initial room creation: first let the scene and provider finish _Ready, then
            // capture their final authored pose as the baseline for the player's custom transform.
            CaptureLocalPlayerBaseline(replacement);
            ApplyLocalPlayerTransform(replacement, groupId);

            previous.GetParent()?.RemoveChild(previous);
            ShopProviderRoots.Remove(previous.GetInstanceId());
            previous.QueueFree();
            refreshedVisual = replacement;
            replacement = null;
            listSwapped = false;
            replacementAdded = false;

            return true;
        }
        catch (Exception exception)
        {
            // A failed character scene or provider callback must leave the live shop exactly as
            // it was. Previously the old node had already been queued for deletion, so the caller
            // received an error while the room kept a half-created replacement.
            foreach (var replayedRoot in replayedRoots.Distinct().ToArray())
            {
                if (!GodotObject.IsInstanceValid(replayedRoot))
                {
                    continue;
                }

                replayedRoot.GetParent()?.RemoveChild(replayedRoot);
                replayedRoot.Free();
            }

            if (replacement != null && GodotObject.IsInstanceValid(replacement))
            {
                ShopProviderRoots.Remove(replacement.GetInstanceId());
                if (replacementAdded)
                {
                    replacement.GetParent()?.RemoveChild(replacement);
                }
                replacement.Free();
                replacement = null;
            }

            if (listSwapped && previous != null && GodotObject.IsInstanceValid(previous))
            {
                visuals[0] = previous;
                previous.Name = previousName;
                if (parent != null && previous.GetParent() == parent && previousIndex >= 0)
                {
                    parent.MoveChild(previous, previousIndex);
                }
            }

            error = exception.GetBaseException().Message;
            ModLog.Error("刷新商店角色外观失败：" + exception);
            return false;
        }
        finally
        {
            if (replacement != null && GodotObject.IsInstanceValid(replacement))
            {
                replacement.Free();
            }
        }
    }

    private static string GetLocalPlayerTransformKey(string groupId) =>
        $"{groupId}::merchant_pose";

    private static void CaptureLocalPlayerBaseline(NMerchantCharacter visual)
    {
        if (!visual.HasMeta(PlayerBasePositionMeta))
        {
            visual.SetMeta(PlayerBasePositionMeta, visual.Position);
        }

        if (!visual.HasMeta(PlayerBaseScaleMeta))
        {
            visual.SetMeta(PlayerBaseScaleMeta, visual.Scale);
        }
    }

    private static void ApplyProviderRootsTransform(
        NMerchantCharacter visual,
        CharacterCombatTransform transform)
    {
        if (!ShopProviderRoots.TryGetValue(visual.GetInstanceId(), out var roots))
        {
            return;
        }

        foreach (var reference in roots.ToArray())
        {
            if (!reference.TryGetTarget(out var root) || !GodotObject.IsInstanceValid(root))
            {
                roots.Remove(reference);
                continue;
            }

            if (!root.HasMeta(ProviderRootBasePositionMeta))
            {
                root.SetMeta(ProviderRootBasePositionMeta, root.Position);
            }

            if (!root.HasMeta(ProviderRootBaseScaleMeta))
            {
                root.SetMeta(ProviderRootBaseScaleMeta, root.Scale);
            }

            var basePosition = root
                .GetMeta(ProviderRootBasePositionMeta, root.Position)
                .AsVector2();
            var baseScale = root
                .GetMeta(ProviderRootBaseScaleMeta, root.Scale)
                .AsVector2();
            root.Position = basePosition + new Vector2(transform.OffsetX, transform.OffsetY);
            root.Scale = baseScale * transform.Scale;
        }

        if (roots.Count == 0)
        {
            ShopProviderRoots.Remove(visual.GetInstanceId());
        }
    }

    private static void ReplaceMerchantButton(
        NMerchantRoom room,
        NMerchantButton replacement,
        out NMerchantButton previous)
    {
        previous = room.MerchantButton ??
                   throw new InvalidOperationException("merchant button is unavailable");
        ReplaceMerchantButtonCore(
            previous,
            replacement,
            room,
            MerchantButtonField!,
            button => room.OpenInventory());
    }

    private static void ReplaceMerchantButtonCore(
        NMerchantButton previous,
        NMerchantButton replacement,
        object targetOwner,
        FieldInfo targetField,
        Action<NMerchantButton> openedCallback)
    {
        var parent = previous.GetParent() ??
                     throw new InvalidOperationException("merchant button has no parent");
        var index = previous.GetIndex();
        var originalName = previous.Name;
        var wasFocused = previous.HasFocus();
        CopyControlRuntimeState(previous, replacement);
        CopyRequiredUniqueNode(previous, replacement, "MerchantSelectionReticle");
        SetOwnerRecursive(replacement, replacement);
        if (replacement.GetNodeOrNull<Node>("%MerchantVisual") == null ||
            replacement.GetNodeOrNull<Node>("%MerchantSelectionReticle") == null)
        {
            throw new InvalidOperationException("商人场景缺少 MerchantVisual 或 MerchantSelectionReticle");
        }

        previous.Name = originalName + "__SkinChangerPrevious";
        replacement.Name = originalName;
        replacement.IsLocalPlayerDead = previous.IsLocalPlayerDead;
        replacement.PlayerDeadLines = previous.PlayerDeadLines;
        parent.AddChild(replacement);
        parent.MoveChild(replacement, index);
        CopyControlRuntimeState(previous, replacement);
        if (replacement.GetNodeOrNull<Node>("%MerchantVisual") == null ||
            replacement.GetNodeOrNull<Node>("%MerchantSelectionReticle") == null)
        {
            throw new InvalidOperationException("商人场景初始化后丢失必要的视觉节点");
        }

        replacement.SetEnabled(previous.IsEnabled);
        replacement.Connect(
            NMerchantButton.SignalName.MerchantOpened,
            Callable.From<NMerchantButton>(openedCallback));
        targetField.SetValue(targetOwner, replacement);
        if (wasFocused)
        {
            replacement.GrabFocus();
        }
    }

    internal static NMerchantButton InstantiateMerchantButton(
        string groupId = GroupId)
    {
        var scenePath = GetMerchantScenePath(groupId);
        // Build the same complete scene that the game uses for this entity. In particular, the
        // fake merchant has its own MerchantButton offsets and skeleton inside fake_merchant.tscn;
        // loading the normal standalone button made its default preview indistinguishable from
        // the real merchant. Keep load+instantiate inside the selected provider overlay so lazy
        // Spine dependencies also come from the selected skin.
        var sceneTemplate = SkinService.InstantiateRuntimeScene<Node>(groupId, scenePath);

        try
        {
            var button = sceneTemplate.GetNodeOrNull<NMerchantButton>("SceneContainer/MerchantButton") ??
                         sceneTemplate.FindChild(
                             "MerchantButton",
                             recursive: true,
                             owned: false) as NMerchantButton ??
                         throw new InvalidOperationException(
                             $"原生商人场景缺少 SceneContainer/MerchantButton：{scenePath}");
            button.GetParent()?.RemoveChild(button);
            // Give the extracted tree its own scene owner before freeing the temporary full
            // scene; otherwise %MerchantVisual
            // and %MerchantSelectionReticle resolve to the freed template and the button's
            // _Ready path receives Nil.
            SetOwnerRecursive(button, button);
            return button;
        }
        finally
        {
            if (GodotObject.IsInstanceValid(sceneTemplate))
            {
                sceneTemplate.Free();
            }
        }
    }

    internal static (Node Scene, string ScenePath) InstantiateMerchantPreviewScene(
        string groupId = GroupId)
    {
        var scenePath = GetMerchantScenePath(groupId);
        // Keep the native root type while previewing. Provider code uses the ancestor type to
        // distinguish a real merchant from the fake merchant (Merchant2Cute, for example,
        // chooses a different scale/offset for NMerchantRoom versus NFakeMerchant). The root
        // _Ready method is skipped by the preview Harmony prefix below, so no run/map/inventory
        // state is touched; every visual child and authored hierarchy remains intact.
        Node scene = groupId.Equals(FakeMerchantGroupId, StringComparison.OrdinalIgnoreCase)
            ? SkinService.InstantiateRuntimeScene<NFakeMerchant>(groupId, scenePath)
            : SkinService.InstantiateRuntimeScene<NMerchantRoom>(groupId, scenePath);
        scene.SetMeta(MerchantPreviewRootMeta, true);
        return (scene, scenePath);
    }

    internal static string GetMerchantScenePath(string groupId = GroupId) =>
        groupId.Equals(FakeMerchantGroupId, StringComparison.OrdinalIgnoreCase)
            ? FakeMerchantScenePath
            : MerchantRoomScenePath;

    private static PackedScene LoadRuntimeOrBaseScene(
        string scenePath,
        string groupId = GroupId) =>
        TryLoadRuntimeOrBaseScene(scenePath, groupId) ??
        throw new InvalidOperationException($"无法加载商店场景：{scenePath}");

    private static PackedScene? TryLoadRuntimeOrBaseScene(
        string scenePath,
        string groupId = GroupId)
    {
        try
        {
            var runtime = SkinService.GetOrLoadRuntimeScene(groupId, scenePath);
            if (runtime != null)
            {
                return runtime;
            }
        }
        catch (Exception exception)
        {
            // The selected provider may only supply code patches, or this path may not exist in
            // the current game version. Fall through to the base-game scene rather than leaving
            // the live shop half-replaced.
            ModLog.Warn($"加载商人场景 {scenePath} 失败，使用游戏场景兜底：" +
                        exception.GetBaseException().Message);
        }

        return ResourceLoader.Load<PackedScene>(
            scenePath,
            null,
            ResourceLoader.CacheMode.Reuse);
    }

    private static void MakeProviderInventoryVisualsPassThrough()
    {
        foreach (var reference in ReplayedInventoryAdditions.ToArray())
        {
            if (!reference.TryGetTarget(out var node) ||
                !GodotObject.IsInstanceValid(node))
            {
                continue;
            }

            foreach (var descendant in EnumerateNodeTree(node))
            {
                if (descendant is Control control)
                {
                    control.MouseFilter = Control.MouseFilterEnum.Ignore;
                }
            }
        }
    }

    private static void ReplaceMerchantHand(
        NMerchantInventory inventory,
        Node2D replacementContainer,
        out Node2D previousContainer,
        out NMerchantHand previousHand)
    {
        previousHand = inventory.MerchantHand;
        if (!GodotObject.IsInstanceValid(previousHand))
        {
            previousHand = FindMerchantHand(inventory) ??
                           throw new InvalidOperationException("当前商店库存缺少 MerchantHand 节点");
            MerchantHandField!.SetValue(inventory, previousHand);
        }

        previousContainer = previousHand.GetParent() as Node2D ??
                            throw new InvalidOperationException("当前 MerchantHand 没有容器节点");
        var parent = previousContainer.GetParent() ??
                     throw new InvalidOperationException("merchant hand has no parent");
        var index = previousContainer.GetIndex();
        var originalName = previousContainer.Name;
        CopyCanvasItemVisualState(previousContainer, replacementContainer);

        SetOwnerRecursive(replacementContainer, replacementContainer);
        var replacementHand = FindMerchantHand(replacementContainer) ??
                              throw new InvalidOperationException("替换后的商人手部缺少 MerchantHand 节点");
        previousContainer.Name = originalName + "__SkinChangerPrevious";
        replacementContainer.Name = originalName;
        parent.AddChild(replacementContainer);
        parent.MoveChild(replacementContainer, index);
        if (!GodotObject.IsInstanceValid(replacementHand) ||
            !replacementHand.IsInsideTree())
        {
            throw new InvalidOperationException("MerchantHand 在场景初始化后失效");
        }

        MerchantHandField!.SetValue(inventory, replacementHand);
    }

    private static NMerchantHand? FindMerchantHand(Node root) =>
        EnumerateNodeTree(root).OfType<NMerchantHand>().FirstOrDefault();

    private static void CopyCanvasItemVisualState(CanvasItem source, CanvasItem target)
    {
        target.Visible = source.Visible;
        target.Modulate = source.Modulate;
        target.SelfModulate = source.SelfModulate;
        target.ZIndex = source.ZIndex;
        target.ZAsRelative = source.ZAsRelative;
    }

    private static void CopyControlRuntimeState(Control source, Control target)
    {
        // Position, size, scale, rotation and pivot are authored by the newly selected scene.
        // Copying them from the live previous skin leaks provider-specific transforms and makes
        // repeated hot swaps accumulate offsets. Only preserve transient UI state.
        CopyCanvasItemVisualState(source, target);
        target.MouseFilter = source.MouseFilter;
        target.FocusMode = source.FocusMode;
    }

    private static void CopyRequiredUniqueNode(Node baseline, Node replacement, string uniqueName)
    {
        if (replacement.GetNodeOrNull<Node>('%' + uniqueName) != null)
        {
            return;
        }

        var source = baseline.GetNodeOrNull<Node>('%' + uniqueName);
        var clone = source?.Duplicate();
        if (clone == null)
        {
            return;
        }

        clone.Name = uniqueName;
        clone.UniqueNameInOwner = true;
        replacement.AddChild(clone);
    }

    private static void SetOwnerRecursive(Node node, Node owner)
    {
        if (!ReferenceEquals(node, owner))
        {
            node.Owner = owner;
        }

        foreach (var child in node.GetChildren())
        {
            SetOwnerRecursive(child, owner);
        }
    }

    private static IEnumerable<Node> EnumerateNodeTree(Node root)
    {
        yield return root;
        foreach (var child in root.GetChildren())
        {
            foreach (var descendant in EnumerateNodeTree(child))
            {
                yield return descendant;
            }
        }
    }
}

[HarmonyPatch(typeof(NMerchantRoom), nameof(NMerchantRoom.Create))]
internal static class MerchantRoomCreateAppearancePatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix() =>
        CharacterAppearanceRuntime.FocusRuntimeProviderBehaviorsOnRunContext(
            [MerchantRuntimeAppearance.GroupId],
            "商店");

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(ref NMerchantRoom? __result)
    {
        if (__result == null || !GodotObject.IsInstanceValid(__result))
        {
            return;
        }

        if (!MerchantRuntimeAppearance.TryCreateCurrentMerchantRoom(
                __result,
                out var currentRoom,
                out var error) ||
            currentRoom == null)
        {
            ModLog.Warn("保留游戏预加载的商店房间：" + error);
            return;
        }

        var preloadCreatedRoom = __result;
        __result = currentRoom;
        preloadCreatedRoom.Free();
        ModLog.Info(
            "商店房间已绕过启动预加载缓存并按当前选择创建：" +
            SkinService.Config.GetSelection(MerchantRuntimeAppearance.GroupId));
    }
}

[HarmonyPatch(typeof(NMerchantRoom), nameof(NMerchantRoom._Ready))]
internal static class MerchantRoomPlayerAppearancePatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(NMerchantRoom __instance) =>
        // The compendium keeps the real NMerchantRoom type so provider patches can distinguish
        // it from NFakeMerchant. Its gameplay _Ready would otherwise touch map/run state, so only
        // the preview instance is skipped; live rooms keep the game's original lifecycle.
        !MerchantRuntimeAppearance.IsMerchantPreviewRoot(__instance);

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(NMerchantRoom __instance)
    {
        if (MerchantRuntimeAppearance.IsMerchantPreviewRoot(__instance))
        {
            return;
        }

        var player = CharacterAppearanceRuntime.GetLocalPlayer();
        if (player == null || __instance.PlayerVisuals.Count == 0)
        {
            return;
        }

        var group = ContextualSkinControls.FindGroup(
            player.Character.Id.Entry,
            player.Character.GetType().Name);
        if (group != null)
        {
            MerchantRuntimeAppearance.ApplyLocalPlayerTransform(
                __instance.PlayerVisuals[0],
                group.Id);

            var providerId = MerchantRuntimeAppearance.GetSelectedLocalCharacterProvider();
            foreach (var replay in ManagedSkinModLoader.ReplaySelectedRoomReadyBehaviors(__instance, providerId))
            {
                MerchantRuntimeAppearance.TrackProviderRoots(
                    __instance.PlayerVisuals[0],
                    replay.AddedRoots);
            }

            MerchantRuntimeAppearance.ApplyLocalPlayerTransform(
                __instance.PlayerVisuals[0],
                group.Id);
        }

    }
}

[HarmonyPatch(typeof(NMerchantRoom), nameof(NMerchantRoom._EnterTree))]
internal static class MerchantRoomPreviewEnterTreePatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(NMerchantRoom __instance) =>
        !MerchantRuntimeAppearance.IsMerchantPreviewRoot(__instance);
}

[HarmonyPatch(typeof(NMerchantRoom), nameof(NMerchantRoom._ExitTree))]
internal static class MerchantRoomPreviewExitTreePatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(NMerchantRoom __instance) =>
        !MerchantRuntimeAppearance.IsMerchantPreviewRoot(__instance);

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(NMerchantRoom __instance)
    {
        if (!MerchantRuntimeAppearance.IsMerchantPreviewRoot(__instance))
        {
            CharacterAppearanceRuntime.FocusRuntimeProviderBehaviorsOnRunCharacters();
        }
    }
}

[HarmonyPatch(typeof(NFakeMerchant), nameof(NFakeMerchant._Ready))]
internal static class FakeMerchantAppearancePatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(NFakeMerchant __instance)
    {
        if (MerchantRuntimeAppearance.IsMerchantPreviewRoot(__instance))
        {
            return false;
        }

        CharacterAppearanceRuntime.FocusRuntimeProviderBehaviorsOnRunContext(
            [MerchantRuntimeAppearance.FakeMerchantGroupId],
            "假商人场景");
        return true;
    }

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(NFakeMerchant __instance)
    {
        if (MerchantRuntimeAppearance.IsMerchantPreviewRoot(__instance))
        {
            return;
        }

        try
        {
            var providerId = SkinService.GetSelectedFullRuntimeProvider("fake_merchant_monster");
            ManagedSkinModLoader.ReplaySelectedRoomReadyBehaviors(__instance, providerId);
        }
        catch (Exception exception)
        {
            ModLog.Warn("重放假商人外观初始化失败：" + exception.GetBaseException().Message);
        }
    }
}

[HarmonyPatch(typeof(NFakeMerchant), nameof(NFakeMerchant._EnterTree))]
internal static class FakeMerchantPreviewEnterTreePatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(NFakeMerchant __instance) =>
        !MerchantRuntimeAppearance.IsMerchantPreviewRoot(__instance);
}

[HarmonyPatch(typeof(NFakeMerchant), nameof(NFakeMerchant._ExitTree))]
internal static class FakeMerchantPreviewExitTreePatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(NFakeMerchant __instance) =>
        !MerchantRuntimeAppearance.IsMerchantPreviewRoot(__instance);

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(NFakeMerchant __instance)
    {
        if (!MerchantRuntimeAppearance.IsMerchantPreviewRoot(__instance))
        {
            CharacterAppearanceRuntime.FocusRuntimeProviderBehaviorsOnRunCharacters();
        }
    }
}

[HarmonyPatch(typeof(NRestSiteRoom), nameof(NRestSiteRoom._Ready))]
internal static class RestSitePlayerAppearancePatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(NRestSiteRoom __instance)
    {
        var providerId = MerchantRuntimeAppearance.GetSelectedLocalCharacterProvider();
        ManagedSkinModLoader.ReplaySelectedRoomReadyBehaviors(__instance, providerId);
    }
}

[HarmonyPatch(typeof(NMerchantButton), nameof(NMerchantButton._Ready))]
internal static class MerchantButtonProviderReadyBridgePatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix(NMerchantButton __instance, out string? __state) =>
        __state = MerchantRuntimeAppearance.BeginSelectedMerchantNodeReady(__instance);

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(NMerchantButton __instance, string? __state) =>
        MerchantRuntimeAppearance.CompleteSelectedMerchantNodeReady(__instance, __state);
}

[HarmonyPatch(typeof(NMerchantInventory), nameof(NMerchantInventory._Ready))]
internal static class MerchantInventoryProviderReadyBridgePatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix(NMerchantInventory __instance, out string? __state) =>
        __state = MerchantRuntimeAppearance.BeginSelectedMerchantNodeReady(__instance);

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(NMerchantInventory __instance, string? __state) =>
        MerchantRuntimeAppearance.CompleteSelectedMerchantNodeReady(__instance, __state);
}

[HarmonyPatch(typeof(NMerchantHand), nameof(NMerchantHand._Ready))]
internal static class MerchantHandProviderReadyBridgePatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix(NMerchantHand __instance, out string? __state) =>
        __state = MerchantRuntimeAppearance.BeginSelectedMerchantNodeReady(__instance);

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(NMerchantHand __instance, string? __state) =>
        MerchantRuntimeAppearance.CompleteSelectedMerchantNodeReady(__instance, __state);
}

// The catalogue uses the real NMerchantInventory scene so hover tips, hand pointing and the
// open/close animation remain native. It has no Player/RunManager transaction context, however,
// so allowing a slot's purchase task to run would invoke MerchantEntry.OnTryPurchase and produce
// null-reference errors. Return a completed task only for preview slots; live shop purchases are
// untouched.
[HarmonyPatch(typeof(NMerchantSlot), "OnSelected")]
internal static class MerchantPreviewPurchaseBlockPatch
{
    [HarmonyPrefix]
    private static bool Prefix(NMerchantSlot __instance, ref Task __result)
    {
        if (!IsPreviewDescendant(__instance))
        {
            return true;
        }

        __result = Task.CompletedTask;
        return false;
    }

    private static bool IsPreviewDescendant(Node node)
    {
        for (Node? current = node; current != null; current = current.GetParent())
        {
            if (MerchantRuntimeAppearance.IsMerchantPreviewRoot(current))
            {
                return true;
            }
        }

        return false;
    }
}
