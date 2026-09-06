using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Vfx;

namespace STS2SkinChanger.Core;

[HarmonyPatch(typeof(NNecrobinderVfx), nameof(NNecrobinderVfx._Ready))]
internal static class NecrobinderOptionalHeadReadyPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        // Cosmetic scenes may intentionally remove HeadBoneNode while retaining the native
        // scythe controller. Keep the native Ready implementation and all its event connections;
        // only make the missing head attachment optional, just like its two particle slots.
        var optional = typeof(Node).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(method => method.Name == nameof(Node.GetNodeOrNull) && method.IsGenericMethodDefinition &&
                              method.GetParameters() is [{ ParameterType: var type }] && type == typeof(NodePath))
            .MakeGenericMethod(typeof(Node2D));
        foreach (var instruction in instructions)
        {
            if (instruction.operand is MethodInfo method && method.DeclaringType == typeof(Node) &&
                method.Name == nameof(Node.GetNode) && method.IsGenericMethod &&
                method.GetGenericArguments().SequenceEqual(new[] { typeof(Node2D) }))
                instruction.operand = optional;
            yield return instruction;
        }
    }
}

[HarmonyPatch(typeof(NNecrobinderVfx), "UpdateFlameVisibility")]
internal static class NecrobinderOptionalHeadAnimationPatch
{
    private static bool Prefix(Node2D? ____headRef) =>
        ____headRef != null && GodotObject.IsInstanceValid(____headRef) && !____headRef.IsQueuedForDeletion();
}
