using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace STS2SkinChanger.Core;

/// <summary>
/// SC's transform wrapper is transparent only to custom visuals querying their NCreature owner.
/// Ordinary parent queries and nodes not wrapped by SC keep Godot's real hierarchy.
/// </summary>
internal static class CreatureVisualParentBridge
{
    private static readonly ConditionalWeakTable<NCreatureVisuals, Binding> Owners = new();
    private static readonly HashSet<Assembly> Inspected = [];
    private static readonly Harmony Patches = new(Entry.ModId + ".visual_parent");

    internal static void Register(NCreature creature, Node2D wrapper)
    {
        var visuals = creature.Visuals;
        Owners.Remove(visuals);
        Owners.Add(visuals, new(wrapper, creature));
        var assembly = visuals.GetType().Assembly;
        if (assembly == typeof(NCreatureVisuals).Assembly || assembly == typeof(Node).Assembly ||
            assembly == typeof(CreatureVisualParentBridge).Assembly || !Inspected.Add(assembly)) return;
        var count = 0;
        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException exception) { types = exception.Types.OfType<Type>().ToArray(); }
        foreach (var method in types.SelectMany(GetMethods))
        {
            try
            {
                if (method.ContainsGenericParameters || method.GetMethodBody() == null) continue;
                var body = PatchProcessor.GetOriginalInstructions(method);
                if (!Enumerable.Range(0, body.Count).Any(index => IsOwnerLookup(body, index))) continue;
                Patches.Patch(method, transpiler: new HarmonyMethod(typeof(CreatureVisualParentBridge), nameof(Rewrite)));
                count++;
            }
            catch (Exception exception)
            {
                ModLog.Warn($"生物父节点兼容未应用 {method.DeclaringType?.FullName}.{method.Name}：{exception.GetBaseException().Message}");
            }
        }
        if (count > 0) ModLog.Info($"已保留自定义生物的模型父节点契约：{assembly.GetName().Name}，{count} 个查询入口。");
    }

    private static IEnumerable<MethodInfo> GetMethods(Type type)
    {
        try { return type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
            BindingFlags.Static | BindingFlags.DeclaredOnly); }
        catch { return []; }
    }

    private static bool IsOwnerLookup(IReadOnlyList<CodeInstruction> body, int index)
    {
        var instruction = body[index];
        if (instruction.opcode != OpCodes.Call && instruction.opcode != OpCodes.Callvirt ||
            instruction.operand is not MethodInfo method || method.DeclaringType != typeof(Node) ||
            method.Name != nameof(Node.GetParent) || method.GetParameters().Length != 0) return false;
        if (method.IsGenericMethod) return method.GetGenericArguments() is [var type] && type == typeof(NCreature);
        var next = index + 1;
        while (next < body.Count && body[next].opcode == OpCodes.Nop) next++;
        return next < body.Count && (body[next].opcode == OpCodes.Isinst || body[next].opcode == OpCodes.Castclass) &&
               Equals(body[next].operand, typeof(NCreature));
    }

    internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToArray();
        for (var index = 0; index < body.Length; index++)
        {
            var instruction = body[index];
            if (!IsOwnerLookup(body, index)) { yield return instruction; continue; }
            var method = (MethodInfo)instruction.operand;
            // Preserve labels and exception boundaries at the original call site.
            yield return new CodeInstruction(instruction)
            {
                opcode = OpCodes.Call,
                operand = AccessTools.Method(typeof(CreatureVisualParentBridge),
                    method.IsGenericMethod ? nameof(GetCreatureParent) : nameof(GetParent))
            };
        }
    }

    private static NCreature GetCreatureParent(Node node) => (NCreature)GetParent(node);

    private static Node GetParent(Node node)
    {
        var parent = node.GetParent();
        if (node is not NCreatureVisuals visuals || !Owners.TryGetValue(visuals, out var binding) ||
            !binding.Owner.TryGetTarget(out var owner) || !GodotObject.IsInstanceValid(owner) ||
            !binding.Wrapper.TryGetTarget(out var wrapper) || !GodotObject.IsInstanceValid(wrapper) ||
            wrapper.GetParent() != owner) return parent;
        return Resolve<Node>(visuals, parent, wrapper, owner, owner.Visuals)!;
    }

    internal static T? Resolve<T>(T visual, T? parent, T wrapper, T owner, T? ownerVisual) where T : class =>
        ReferenceEquals(parent, wrapper) && ReferenceEquals(visual, ownerVisual) ? owner : parent;

    private sealed class Binding(Node wrapper, NCreature owner)
    {
        public readonly WeakReference<Node> Wrapper = new(wrapper);
        public readonly WeakReference<NCreature> Owner = new(owner);
    }
}
