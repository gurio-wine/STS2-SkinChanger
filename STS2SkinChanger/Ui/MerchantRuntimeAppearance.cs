using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
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
    private const string MerchantButtonScenePath = "res://scenes/rooms/merchant_button.tscn";
    private const string MerchantRoomScenePath = "res://scenes/rooms/merchant_room.tscn";
    private const string MerchantInventoryScenePath = "res://scenes/merchant/merchant_inventory.tscn";
    private const string PlayerBasePositionMeta = "skin_changer_shop_player_base_position";
    private const string PlayerBaseScaleMeta = "skin_changer_shop_player_base_scale";
    private const string ProviderRootBasePositionMeta = "skin_changer_shop_provider_root_position";
    private const string ProviderRootBaseScaleMeta = "skin_changer_shop_provider_root_scale";

    private static readonly FieldInfo? MerchantButtonField =
        AccessTools.Field(typeof(NMerchantRoom), "<MerchantButton>k__BackingField");
    private static readonly FieldInfo? MerchantHandField =
        AccessTools.Field(typeof(NMerchantInventory), "<MerchantHand>k__BackingField");
    private static readonly List<WeakReference<Node>> ReplayedInventoryAdditions = [];
    private static readonly Dictionary<ulong, HashSet<ulong>> InventoryReadyBaselines = [];
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
        return SkinService.SetCharacterCombatTransform(
            GetLocalPlayerTransformKey(groupId),
            optionId,
            value,
            save);
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

    internal static void CaptureInventoryReadyBaseline(NMerchantInventory inventory)
    {
        if (SkinService.GetSelectedFullRuntimeProvider(GroupId) == null)
        {
            return;
        }

        InventoryReadyBaselines[inventory.GetInstanceId()] = EnumerateNodeTree(inventory)
            .Select(node => node.GetInstanceId())
            .ToHashSet();
    }

    internal static void TrackInventoryReadyAdditions(NMerchantInventory inventory)
    {
        if (!InventoryReadyBaselines.Remove(inventory.GetInstanceId(), out var baselineIds))
        {
            return;
        }

        foreach (var node in EnumerateNodeTree(inventory)
                     .Where(node => !baselineIds.Contains(node.GetInstanceId()))
                     .Where(node => node.GetParent() is not { } parent ||
                                    baselineIds.Contains(parent.GetInstanceId())))
        {
            ReplayedInventoryAdditions.Add(new WeakReference<Node>(node));
        }

        MakeProviderInventoryVisualsPassThrough();
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
            inventoryTemplate = LoadRuntimeOrBaseScene(MerchantInventoryScenePath)
                .Instantiate<NMerchantInventory>(PackedScene.GenEditState.Disabled);
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
                foreach (var addedNode in ManagedSkinModLoader.ReplaySelectedNodeReadyBehavior(
                             providerId,
                             room.Inventory))
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
        try
        {
            var previous = visuals[0];
            CaptureLocalPlayerBaseline(previous);
            var basePosition = previous
                .GetMeta(PlayerBasePositionMeta, previous.Position)
                .AsVector2();
            var parent = previous.GetParent() ??
                         throw new InvalidOperationException("merchant player visual has no parent");
            var index = previous.GetIndex();
            replacement = SkinService
                .GetOrLoadRuntimeScene(groupId, player.Character.MerchantAnimPath)
                .Instantiate<NMerchantCharacter>(PackedScene.GenEditState.Disabled);
            replacement.Position = basePosition;
            replacement.Visible = previous.Visible;
            replacement.Modulate = previous.Modulate;
            replacement.SelfModulate = previous.SelfModulate;
            replacement.ZIndex = previous.ZIndex;
            replacement.ZAsRelative = previous.ZAsRelative;
            CaptureLocalPlayerBaseline(replacement);
            ApplyLocalPlayerTransform(replacement, groupId);

            previous.Name = previous.Name + "Previous";
            parent.AddChild(replacement);
            parent.MoveChild(replacement, index);
            visuals[0] = replacement;
            previous.GetParent()?.RemoveChild(previous);
            previous.QueueFree();
            refreshedVisual = replacement;
            replacement = null;

            // Complete character skins can attach a separate shop presentation from an
            // NMerchantRoom._Ready postfix. That callback already ran when the room was first
            // created, so replay it after a hot swap as well; the loader tracks its added nodes
            // and visibility changes and restores them when the provider is left.
            var providerId = SkinService.GetSelectedFullRuntimeProvider(groupId);
            if (providerId != null)
            {
                foreach (var replay in ManagedSkinModLoader.ReplaySelectedRoomReadyBehaviors(room, providerId))
                {
                    TrackProviderRoots(refreshedVisual!, replay.AddedRoots);
                }

                ApplyLocalPlayerTransform(refreshedVisual!, groupId);
            }

            return true;
        }
        catch (Exception exception)
        {
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
        var parent = previous.GetParent() ??
                     throw new InvalidOperationException("merchant button has no parent");
        var index = previous.GetIndex();
        var originalName = previous.Name;
        var wasFocused = previous.HasFocus();
        CopyControlVisualState(previous, replacement);
        ManagedSceneCompatibility.CopyMissingUniqueNodes(previous, replacement);
        CopyRequiredUniqueNode(previous, replacement, "MerchantVisual");
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
        CopyControlVisualState(previous, replacement);
        if (replacement.GetNodeOrNull<Node>("%MerchantVisual") == null ||
            replacement.GetNodeOrNull<Node>("%MerchantSelectionReticle") == null)
        {
            throw new InvalidOperationException("商人场景初始化后丢失必要的视觉节点");
        }

        replacement.SetEnabled(previous.IsEnabled);
        replacement.Connect(
            NMerchantButton.SignalName.MerchantOpened,
            Callable.From<NMerchantButton>(_ => room.OpenInventory()));
        MerchantButtonField!.SetValue(room, replacement);
        if (wasFocused)
        {
            replacement.GrabFocus();
        }
    }

    private static NMerchantButton InstantiateMerchantButton()
    {
        // v0.111.0 introduced a standalone merchant_button.tscn. Older formal builds keep the
        // same node embedded in merchant_room.tscn, so use the standalone scene when available
        // and extract the embedded node as a version-neutral fallback.
        var standalone = TryLoadRuntimeOrBaseScene(MerchantButtonScenePath);
        if (standalone != null)
        {
            var button = standalone.Instantiate<NMerchantButton>(PackedScene.GenEditState.Disabled);
            SetOwnerRecursive(button, button);
            return button;
        }

        var roomScene = LoadRuntimeOrBaseScene(MerchantRoomScenePath);
        var roomTemplate = roomScene.Instantiate<NMerchantRoom>(PackedScene.GenEditState.Disabled);
        try
        {
            var button = roomTemplate.GetNode<NMerchantButton>("SceneContainer/MerchantButton");
            button.GetParent()?.RemoveChild(button);
            // The formal build embeds this node in merchant_room.tscn. Give the extracted tree
            // its own scene owner before freeing the temporary room; otherwise %MerchantVisual
            // and %MerchantSelectionReticle resolve to the freed template and the button's
            // _Ready path receives Nil.
            SetOwnerRecursive(button, button);
            return button;
        }
        finally
        {
            if (GodotObject.IsInstanceValid(roomTemplate))
            {
                roomTemplate.Free();
            }
        }
    }

    private static PackedScene LoadRuntimeOrBaseScene(string scenePath) =>
        TryLoadRuntimeOrBaseScene(scenePath) ??
        throw new InvalidOperationException($"无法加载商店场景：{scenePath}");

    private static PackedScene? TryLoadRuntimeOrBaseScene(string scenePath)
    {
        try
        {
            var runtime = SkinService.GetOrLoadRuntimeScene(GroupId, scenePath);
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
        CopyNode2DVisualState(previousContainer, replacementContainer);
        ManagedSceneCompatibility.CopyMissingUniqueNodes(previousContainer, replacementContainer);
        if (FindMerchantHand(replacementContainer) == null)
        {
            var handClone = previousHand.Duplicate();
            if (handClone != null)
            {
                handClone.Name = previousHand.Name;
                handClone.UniqueNameInOwner = true;
                replacementContainer.AddChild(handClone);
            }
        }

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

    private static void CopyControlVisualState(Control source, Control target)
    {
        CopyCanvasItemVisualState(source, target);
        target.Position = source.Position;
        target.Size = source.Size;
        target.Scale = source.Scale;
        target.Rotation = source.Rotation;
        target.PivotOffset = source.PivotOffset;
        target.MouseFilter = source.MouseFilter;
        target.FocusMode = source.FocusMode;
    }

    private static void CopyNode2DVisualState(Node2D source, Node2D target)
    {
        CopyCanvasItemVisualState(source, target);
        target.Position = source.Position;
        target.Scale = source.Scale;
        target.Rotation = source.Rotation;
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

[HarmonyPatch(typeof(NMerchantRoom), nameof(NMerchantRoom._Ready))]
internal static class MerchantRoomPlayerAppearancePatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(NMerchantRoom __instance)
    {
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

[HarmonyPatch(typeof(NMerchantInventory), nameof(NMerchantInventory._Ready))]
internal static class MerchantInventoryAppearanceTrackingPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix(NMerchantInventory __instance) =>
        MerchantRuntimeAppearance.CaptureInventoryReadyBaseline(__instance);

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(NMerchantInventory __instance) =>
        MerchantRuntimeAppearance.TrackInventoryReadyAdditions(__instance);
}
