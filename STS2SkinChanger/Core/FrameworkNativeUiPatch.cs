using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;

namespace STS2SkinChanger.Core;

/// <summary>Compatibility for an unused debug lookup in the native injector, not a replacement UI.</summary>
internal static class FrameworkNativeUiPatch
{
    public static void Install(Assembly assembly, Harmony harmony)
    {
        var injector = assembly.GetType("thunninoiSkinManager.thunninoiSkinManagerCode.SkinSelectorInjector", true)!;
        harmony.Patch(AccessTools.Method(injector, "Postfix"),
            transpiler: new HarmonyMethod(typeof(FrameworkNativeUiPatch), nameof(RemoveDebugLookup)));
        // The native entry callback is exceptional: unlike its other rendering callbacks it
        // never consults SkinRegistry. Limit this skin effect to its selected provider instead
        // of injecting an extra entry state into every unrelated character/skin.
        var entry = assembly.GetType("thunninoiSkinManager.thunninoiSkinManagerCode.Patches.EntryAnimationAdd", true)!;
        harmony.Patch(AccessTools.Method(entry, "AddEntry"),
            prefix: new HarmonyMethod(typeof(FrameworkNativeUiPatch), nameof(OwnsEntryAnimation)));
    }

    private static bool OwnsEntryAnimation(CharacterModel __1) =>
        SkinService.TryGetSelectedFrameworkContract(FrameworkSkinRuntime.NormalizeToken(__1.Id.Entry), out var contract) &&
        FrameworkRegistryCooperation.UsesNativePresentation(contract);

    internal static IEnumerable<CodeInstruction> RemoveDebugLookup(IEnumerable<CodeInstruction> source)
    {
        var instructions = source.ToList();
        var index = instructions.FindIndex(instruction => instruction.operand is MethodInfo method &&
            method.Name == "AddChildSafely" && method.DeclaringType?.FullName == "MegaCrit.Sts2.Core.Helpers.GodotTreeExtensions");
        // Exact suffix shape from the original package: AddChildSafely; ldarg.0; ldstr <debug>.
        // Retain every functional instruction. Future different implementations are untouched.
        if (index >= 0 && index + 2 < instructions.Count && instructions[index + 1].opcode == OpCodes.Ldarg_0 &&
            instructions[index + 2].opcode == OpCodes.Ldstr &&
            Equals(instructions[index + 2].operand, "CharSelectButtons/ButtonContainer/DEFECT_button/PlayerIconContainer") &&
            instructions.Take(index + 1).All(instruction => instruction.blocks.Count == 0))
            return instructions.Take(index + 1).Append(new CodeInstruction(OpCodes.Ret));
        return instructions;
    }
}
