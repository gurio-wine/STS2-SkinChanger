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
        try
        {
            newButton = SkinService
                .GetOrLoadRuntimeScene(GroupId, MerchantButtonScenePath)
                .Instantiate<NMerchantButton>(PackedScene.GenEditState.Disabled);
            inventoryTemplate = SkinService
                .GetOrLoadRuntimeScene(GroupId, MerchantInventoryScenePath)
                .Instantiate<NMerchantInventory>(PackedScene.GenEditState.Disabled);
            var templateHand = inventoryTemplate.GetNode<NMerchantHand>("%MerchantHand");
            newHandContainer = templateHand.GetParent<Node2D>();
            inventoryTemplate.RemoveChild(newHandContainer);
            ClearOwnerRecursive(newHandContainer);

            ReplaceMerchantButton(room, newButton);
            newButton = null;
            ReplaceMerchantHand(room.Inventory, newHandContainer);
            newHandContainer = null;

            var providerId = SkinService.GetSelectedFullRuntimeProvider(GroupId);
            if (providerId != null)
            {
                foreach (var addedNode in ManagedSkinModLoader.ReplaySelectedNodeReadyBehavior(
                             providerId,
                             room.Inventory))
                {
                    ReplayedInventoryAdditions.Add(new WeakReference<Node>(addedNode));
                }
            }

            return true;
        }
        catch (Exception exception)
        {
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

    private static void ReplaceMerchantButton(NMerchantRoom room, NMerchantButton replacement)
    {
        var previous = room.MerchantButton;
        var parent = previous.GetParent() ??
                     throw new InvalidOperationException("merchant button has no parent");
        var index = previous.GetIndex();
        var originalName = previous.Name;
        previous.Name = originalName + "Previous";
        replacement.Name = originalName;
        parent.AddChild(replacement);
        parent.MoveChild(replacement, index);
        replacement.IsLocalPlayerDead = previous.IsLocalPlayerDead;
        replacement.PlayerDeadLines = previous.PlayerDeadLines;
        replacement.Connect(
            NMerchantButton.SignalName.MerchantOpened,
            Callable.From<NMerchantButton>(_ => room.OpenInventory()));
        MerchantButtonField!.SetValue(room, replacement);
        previous.GetParent()?.RemoveChild(previous);
        previous.QueueFree();
    }

    private static void ReplaceMerchantHand(
        NMerchantInventory inventory,
        Node2D replacementContainer)
    {
        var previousHand = inventory.MerchantHand;
        var previousContainer = previousHand.GetParent<Node2D>();
        var parent = previousContainer.GetParent() ??
                     throw new InvalidOperationException("merchant hand has no parent");
        var index = previousContainer.GetIndex();
        var originalName = previousContainer.Name;
        previousContainer.Name = originalName + "Previous";
        replacementContainer.Name = originalName;
        parent.AddChild(replacementContainer);
        parent.MoveChild(replacementContainer, index);
        var replacementHand = replacementContainer.GetNode<NMerchantHand>("%MerchantHand");
        MerchantHandField!.SetValue(inventory, replacementHand);
        previousContainer.GetParent()?.RemoveChild(previousContainer);
        previousContainer.QueueFree();
    }

    private static void ClearOwnerRecursive(Node node)
    {
        node.Owner = null;
        foreach (var child in node.GetChildren())
        {
            ClearOwnerRecursive(child);
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
