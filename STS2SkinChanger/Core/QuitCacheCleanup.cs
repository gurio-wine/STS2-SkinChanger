using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;

namespace STS2SkinChanger.Core;

// Intercept the game's final Quit entry, not the confirmation popup. Cancelling the
// popup must do nothing, and the native settings/progress saves still run unchanged.
// A Quit that arrives before this mod has finished initializing is not an exit and is rejected
// without a side effect; see QuitInterceptionPolicy for the reported session behind that.
[HarmonyPatch(typeof(NGame), nameof(NGame.Quit))]
internal static class QuitCacheCleanup
{
    private static readonly QuitCleanupCoordinator Coordinator = new();

    // Called by the entry patch once the whole start-up chain has finished, which is the moment a
    // Quit call starts to mean the player is leaving.
    internal static void MarkInitializationCompleted() => Coordinator.MarkInitializationCompleted();

    // Whether a Quit call currently counts as the player leaving.
    internal static bool AcceptsQuitAsExit => Coordinator.InitializationCompleted;

    private static bool Prefix(NGame __instance) =>
        Coordinator.HandleQuit(
            () => Task.Run(() => QuitExitCleanup.Clean(SkinChangerPaths.Storage)),
            () => { if (GodotObject.IsInstanceValid(__instance)) __instance.Quit(); },
            ModLog.Info);
}
