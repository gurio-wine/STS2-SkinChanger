using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using STS2SkinChanger;

internal static class GameVersionCompatibilityTests
{
    internal static void Run()
    {
        var assembly = typeof(Entry).Assembly;
        _ = assembly.GetTypes(); // A removed game base type must not break patch discovery.
        var game = typeof(AbstractModel).Assembly;
        var scalingType = game.GetType("MegaCrit.Sts2.Core.Models.MultiplayerScalingModel") ??
                          game.GetType("MegaCrit.Sts2.Core.Models.Singleton.MultiplayerScalingModel")!;
        var boundary = AccessTools.Method(
            assembly.GetType("STS2SkinChanger.Ui.StatefulCombatModelBoundary"),
            "IsMultiplayerScalingModel");
        Require((bool)boundary.Invoke(null, [RuntimeHelpers.GetUninitializedObject(scalingType)])!,
            "The current game's multiplayer model must still drive cosmetic observers.");
        Require(!(bool)boundary.Invoke(null, [RuntimeHelpers.GetUninitializedObject(typeof(Ironclad))])!,
            "Unrelated models must not repeat the multiplayer model's cosmetic effects.");

        var roomType = game.GetType("MegaCrit.Sts2.Core.Nodes.Rooms.NStandardMerchantRoom") ?? typeof(NMerchantRoom);
        var harmony = new Harmony("Gurio.SkinChanger.tests.game-version");
        try
        {
            foreach (var (patchName, methodName) in new[]
            {
                ("MerchantRoomCreateAppearancePatch", "Create"),
                ("MerchantRoomPlayerAppearancePatch", "_Ready")
            })
            {
                var patch = assembly.GetType("STS2SkinChanger.Ui." + patchName, true)!;
                var installed = harmony.CreateClassProcessor(patch).Patch();
                var target = roomType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static |
                    BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
                Require(installed.Count == 1 && target != null &&
                        Harmony.GetPatchInfo(target)?.Owners.Contains(harmony.Id) == true,
                    patchName + " must patch the concrete standard shop's " + methodName + ".");
            }
        }
        finally { harmony.UnpatchAll(harmony.Id); }
        CheckPatchInstallation(assembly);
        Console.WriteLine("Game version compatibility passed: model discovery, exact cosmetic observer, concrete merchant lifecycle and all startup patch classes.");
    }

    private static void CheckPatchInstallation(Assembly assembly)
    {
        var harmony = new Harmony("Gurio.SkinChanger.tests.game-version.all-patches");
        var logging = new Harmony("Gurio.SkinChanger.tests.game-version.logging");
        try
        {
            foreach (var name in new[] { "Info", "Warn", "Error" })
                logging.Patch(AccessTools.Method(assembly.GetType("STS2SkinChanger.Core.ModLog"), name),
                    prefix: new HarmonyMethod(typeof(GameVersionCompatibilityTests), nameof(SkipLog)));
            var failed = Entry.InstallPatchClasses(harmony, assembly.GetTypes());
            Require(failed.Count == 0, "Startup patch classes failed on this game: " + string.Join(", ", failed));
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            logging.UnpatchAll(logging.Id);
        }
    }

    private static bool SkipLog(string message)
    {
        Console.WriteLine(message);
        return false;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
