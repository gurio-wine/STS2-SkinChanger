using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;

namespace STS2SkinChanger.Core;

/// <summary>
/// The game builds every shop character in one private loop. Intercepting AssetCache alone cannot
/// tell which player owns the current scene request, so same-character players otherwise inherit
/// the local player's globally selected skin. This mirrors the game's loop while adding only the
/// missing per-player scope.
/// </summary>
[HarmonyPatch]
internal static class MultiplayerMerchantPlayerVisualIsolationPatch
{
    private static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(NMerchantRoom), "AfterRoomIsLoaded") ??
        throw new MissingMethodException(typeof(NMerchantRoom).FullName, "AfterRoomIsLoaded");

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(
        List<Player> ____players,
        Control ____characterContainer,
        List<NMerchantCharacter> ____playerVisuals)
    {
        // Preserve the untouched game path in single player. The replacement loop is only needed
        // when one shared CharacterModel can represent more than one player's skin selection.
        if (____players.Count <= 1)
        {
            return true;
        }

        var localPlayer = LocalContext.GetMe(____players) ?? ____players[0];
        ____players.Remove(localPlayer);
        ____players.Insert(0, localPlayer);

        var gridSize = Mathf.CeilToInt(Mathf.Sqrt(____players.Count));
        for (var row = 0; row < gridSize; row++)
        {
            var x = -140f * row;
            for (var column = 0; column < gridSize; column++)
            {
                var playerIndex = row * gridSize + column;
                if (playerIndex >= ____players.Count)
                {
                    break;
                }

                var player = ____players[playerIndex];
                using var scope = MultiplayerSkinSync.BeginPlayerRuntimeScope(
                    player,
                    player.Character.MerchantAnimPath);
                var visual = PreloadManager.Cache
                    .GetScene(player.Character.MerchantAnimPath)
                    .Instantiate<NMerchantCharacter>(PackedScene.GenEditState.Disabled);
                ____characterContainer.AddChildSafely(visual);
                ____characterContainer.MoveChildSafely(visual, 0);
                visual.Position = new Vector2(x, -50f * row);
                if (row > 0)
                {
                    visual.Modulate = new Color(0.5f, 0.5f, 0.5f);
                }

                x -= 275f;
                ____playerVisuals.Add(visual);
            }
        }

        return false;
    }
}

[HarmonyPatch(typeof(NRestSiteCharacter), nameof(NRestSiteCharacter.Create))]
internal static class MultiplayerRestSiteCreateScopePatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix(Player player, out IDisposable? __state) =>
        __state = MultiplayerSkinSync.BeginPlayerRuntimeScope(
            player,
            player.Character.RestSiteAnimPath);

    private static Exception? Finalizer(Exception? __exception, IDisposable? __state)
    {
        __state?.Dispose();
        return __exception;
    }
}

// Create loads the packed scene, while _Ready resolves its deferred skeletons, materials and
// provider callbacks after the node is attached. Both stages must retain the same owner scope.
[HarmonyPatch(typeof(NRestSiteCharacter), nameof(NRestSiteCharacter._Ready))]
internal static class MultiplayerRestSiteReadyScopePatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(NRestSiteCharacter __instance) =>
        ManagedSlotVisibilityBridge.BindPlayerScene(__instance, __instance.Player);

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix(NRestSiteCharacter __instance, out IDisposable? __state) =>
        __state = __instance.Player == null
            ? null
            : MultiplayerSkinSync.BeginPlayerRuntimeScope(
                __instance.Player,
                __instance.Player.Character.RestSiteAnimPath);

    private static Exception? Finalizer(Exception? __exception, IDisposable? __state)
    {
        __state?.Dispose();
        return __exception;
    }
}

[HarmonyPatch(typeof(NHandImage), nameof(NHandImage._Ready))]
internal static class MultiplayerTreasureHandReadyScopePatch
{
    [HarmonyPostfix]
    private static void Postfix(NHandImage __instance) =>
        MultiplayerTreasureHandAppearance.Remember(__instance, null);

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix(NHandImage __instance, out IDisposable? __state) =>
        __state = __instance.Player == null
            ? null
            : MultiplayerSkinSync.BeginPlayerSelectionScope(__instance.Player.NetId);

    private static Exception? Finalizer(Exception? __exception, IDisposable? __state)
    {
        __state?.Dispose();
        return __exception;
    }
}

// The hand switches textures again during rock-paper-scissors. Without a second scope here the
// correctly initialized remote hand immediately reverts to the local player's skin on its move.
[HarmonyPatch]
internal static class MultiplayerTreasureHandMoveScopePatch
{
    [HarmonyPostfix]
    private static void Postfix(NHandImage __instance, RelicPickingFightMove move) =>
        MultiplayerTreasureHandAppearance.Remember(__instance, move);

    private static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(NHandImage), "SetTextureToFightMove") ??
        throw new MissingMethodException(typeof(NHandImage).FullName, "SetTextureToFightMove");

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix(NHandImage __instance, out IDisposable? __state) =>
        __state = __instance.Player == null
            ? null
            : MultiplayerSkinSync.BeginPlayerSelectionScope(__instance.Player.NetId);

    private static Exception? Finalizer(Exception? __exception, IDisposable? __state)
    {
        __state?.Dispose();
        return __exception;
    }
}

[HarmonyPatch(typeof(NHandImage), nameof(NHandImage._ExitTree))]
internal static class MultiplayerTreasureHandExitPatch
{
    private static void Postfix(NHandImage __instance) => MultiplayerTreasureHandAppearance.Forget(__instance);
}

internal static class MultiplayerTreasureHandAppearance
{
    private sealed class Hand(NHandImage node, RelicPickingFightMove? move)
    {
        internal readonly WeakReference<NHandImage> Node = new(node);
        internal RelicPickingFightMove? Move = move;
    }

    private static readonly List<Hand> Hands = [];
    private static readonly FieldInfo TextureField = AccessTools.Field(typeof(NHandImage), "_textureRect");

    internal static void Remember(NHandImage node, RelicPickingFightMove? move)
    {
        var entry = Hands.FirstOrDefault(hand => hand.Node.TryGetTarget(out var target) && ReferenceEquals(node, target));
        if (entry == null) Hands.Add(new Hand(node, move));
        else entry.Move = move;
    }

    internal static void Forget(NHandImage node) => Hands.RemoveAll(hand =>
        !hand.Node.TryGetTarget(out var target) || ReferenceEquals(node, target));

    internal static bool RefreshPlayer(ulong playerId)
    {
        var refreshed = false;
        foreach (var hand in Hands.ToArray())
        {
            if (!hand.Node.TryGetTarget(out var node) || !GodotObject.IsInstanceValid(node))
            {
                Hands.Remove(hand);
                continue;
            }
            if (node.Player?.NetId != playerId || !node.IsInsideTree() ||
                TextureField.GetValue(node) is not TextureRect texture || !GodotObject.IsInstanceValid(texture)) continue;

            // Only swap the current pose's texture. Replaying _Ready/DoFightMove would reset
            // pivots, positions, tweens and game state during grabbing/rock-paper-scissors.
            using var scope = MultiplayerSkinSync.BeginPlayerSelectionScope(playerId);
            var character = node.Player.Character;
            texture.Texture = hand.Move switch
            {
                RelicPickingFightMove.Rock => character.ArmRockTexture,
                RelicPickingFightMove.Paper => character.ArmPaperTexture,
                RelicPickingFightMove.Scissors => character.ArmScissorsTexture,
                _ => character.ArmPointingTexture
            };
            refreshed = true;
        }
        return refreshed;
    }
}
