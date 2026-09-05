using System.Reflection;
using System.Reflection.Emit;
using Godot;
using HarmonyLib;

namespace STS2SkinChanger.Core;

/// <summary>Recognizes an input-driven boolean slot-alpha toggle, not arbitrary animation flags.</summary>
internal sealed record SlotToggleContract(FieldInfo State, FieldInfo Slots, MethodInfo Input, MethodInfo Apply)
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    public string Id => State.DeclaringType!.FullName + ":" + State.Name;

    public static SlotToggleContract? TryCreate(Type type)
    {
        var input = type.GetMethod("_Input", Instance, [typeof(InputEvent)]);
        var slots = type.GetFields(Instance).Where(field => field.FieldType == typeof(string[])).ToArray();
        if (input?.GetMethodBody() == null || slots.Length != 1) return null;
        var instructions = PatchProcessor.GetOriginalInstructions(input);
        var toggles = new List<FieldInfo>();
        for (var i = 0; i + 3 < instructions.Count; i++)
        {
            if (instructions[i].opcode == OpCodes.Ldfld && instructions[i].operand is FieldInfo field &&
                field.FieldType == typeof(bool) && instructions[i + 1].opcode == OpCodes.Ldc_I4_0 &&
                instructions[i + 2].opcode == OpCodes.Ceq && instructions[i + 3].opcode == OpCodes.Stfld &&
                Equals(instructions[i + 3].operand, field)) toggles.Add(field);
        }
        if (toggles.Count != 1) return null;
        var state = toggles[0];
        foreach (var apply in instructions.Select(instruction => instruction.operand).OfType<MethodInfo>()
                     .Where(method => method.DeclaringType == type && method.ReturnType == typeof(void) &&
                                      method.GetParameters().Length == 0 && method.GetMethodBody() != null).Distinct())
        {
            var body = PatchProcessor.GetOriginalInstructions(apply);
            var literals = body.Where(instruction => instruction.opcode == OpCodes.Ldstr)
                .Select(instruction => instruction.operand as string).ToHashSet();
            if (!literals.Contains("get_color") || !literals.Contains("set_color")) continue;
            // Require the true branch to zero alpha and the false branch to skip it. Merely having
            // an input flag and a color writer is not enough (head-press/animation scripts also do).
            for (var i = 0; i + 1 < body.Count; i++)
            {
                if (body[i].opcode != OpCodes.Ldfld || !Equals(body[i].operand, state)) continue;
                var branch = i + 1;
                // Debug-built providers store a condition in a local before branching.
                if (branch + 2 < body.Count && body[branch].opcode.Name?.StartsWith("stloc", StringComparison.Ordinal) == true &&
                    body[branch + 1].opcode.Name?.StartsWith("ldloc", StringComparison.Ordinal) == true &&
                    LocalIndex(body[branch]) is { } index && index == LocalIndex(body[branch + 1])) branch += 2;
                if ((body[branch].opcode != OpCodes.Brfalse && body[branch].opcode != OpCodes.Brfalse_S) ||
                    body[branch].operand is not System.Reflection.Emit.Label target) continue;
                var end = body.FindIndex(branch + 1, instruction => instruction.labels.Contains(target));
                for (var j = branch + 1; j + 1 < end; j++)
                {
                    if (body[j].opcode == OpCodes.Ldc_R4 && Equals(body[j].operand, 0f) &&
                        body[j + 1].opcode == OpCodes.Stfld && body[j + 1].operand is FieldInfo alpha &&
                        alpha.DeclaringType == typeof(Color) && alpha.Name == nameof(Color.A))
                        return new(state, slots[0], input, apply);
                }
            }
        }
        return null;
    }

    private static int? LocalIndex(CodeInstruction instruction)
    {
        if (instruction.operand is LocalVariableInfo local) return local.LocalIndex;
        var name = instruction.opcode.Name;
        return name is { Length: 7 } && char.IsDigit(name[^1]) ? name[^1] - '0' : null;
    }
}
