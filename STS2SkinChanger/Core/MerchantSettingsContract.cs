using System.Reflection;
using HarmonyLib;
using Godot;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;

namespace STS2SkinChanger.Core;

/// <summary>Audited legacy API contract; no manifest ID or Workshop ID is used for matching.</summary>
internal sealed record MerchantSettingsContract(Type CommandType, MethodInfo Save, MethodInfo ApplyHand,
    MethodInfo ApplyLegs, MethodInfo UseFoot, MethodInfo? SettingsPostfix, IReadOnlyList<MethodInfo> WorldApplyMethods)
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    internal static MerchantSettingsContract? TryCreate(Assembly assembly)
    {
        var types = assembly.GetTypes();
        foreach (var command in types.Where(type => !type.IsAbstract && typeof(AbstractConsoleCmd).IsAssignableFrom(type)))
        {
            var hands = command.GetMethod("ApplyToExistingHands", Static, Type.EmptyTypes);
            var legs = command.GetMethod("UpdateLegVisibility", Static, [typeof(bool)]);
            // Never expose the command unless BOTH original world entry points were adapted
            // before assembly load. In particular, its .cctor must not retain a scene walk.
            if (hands?.ReturnType != typeof(int) || legs?.ReturnType != typeof(void) ||
                !IsAdapted(hands) || !IsAdapted(legs)) continue;
            var applyHand = command.GetMethod("TryApplyToHand", Static, [typeof(NMerchantHand)]);
            var applyLegs = command.GetMethod("UpdateLegVisibilityStatic", Static, [typeof(Node), typeof(bool)]);
            var point = command.GetMethod("ProcessPointCommand", Static, [typeof(string[])]);
            var save = point == null ? null : Calls(point).SingleOrDefault(method => method.Name == "Save" &&
                method.ReturnType == typeof(void) && method.GetParameters().Length == 0 && method.Module.Assembly == assembly);
            var useFoot = command.TypeInitializer == null ? null : Calls(command.TypeInitializer)
                .SingleOrDefault(method => method.Name == "get_UseFootLike" && method.ReturnType == typeof(bool) &&
                                          method.GetParameters().Length == 0 && method.Module.Assembly == assembly);
            if (applyHand?.ReturnType != typeof(bool) || applyLegs?.ReturnType != typeof(void) || save == null || useFoot == null)
                continue;
            // Only this verified legacy settings page is retained. Arbitrary menu/character UI
            // patches do not become persistent just because they draw a button.
            var settings = types.Where(type => type.Namespace == save.DeclaringType?.Namespace + ".Patches")
                .Select(type => type.GetMethod("Postfix", Static, [typeof(NModInfoContainer), typeof(Mod)]))
                .SingleOrDefault(method => method?.DeclaringType?.Name == "ModdingScreenUiPatch");
            return new(command, save, applyHand, applyLegs, useFoot, settings, [hands, legs]);
        }
        return null;
    }

    private static bool IsAdapted(MethodInfo method) => Calls(method).Any(call =>
        call.DeclaringType == typeof(ProviderSettingsApi) && call.Name == nameof(ProviderSettingsApi.Refresh));

    private static IEnumerable<MethodInfo> Calls(MethodBase method) =>
        PatchProcessor.GetOriginalInstructions(method).Select(instruction => instruction.operand).OfType<MethodInfo>().Distinct();
}
