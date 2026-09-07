using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Combat;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

// Keep the provider's baseline and undo only our own last write. A replacement skin
// with ordinary depth must not inherit the previous skin's raised intent layer.
internal sealed class CreatureIntentDepthState
{
    private (int Z, bool Relative)? _baseline;
    private (int Z, bool Relative)? _applied;

    internal (int Z, bool Relative) Resolve(
        int currentZ, bool relative, int parentZ, int visualZ, bool detachedVisual)
    {
        var current = (currentZ, relative);
        if (_baseline == null || _applied != current) _baseline = current;
        var baseline = _baseline.Value;
        var baselineEffective = baseline.Z + (baseline.Relative ? parentZ : 0);
        // With ordinary tree ordering the later intent branch can share the top model
        // Z. Do not unnecessarily raise it above the game's later combat UI at Z=0.
        var required = Math.Max(baselineEffective, visualZ + (detachedVisual ? 1 : 0));
        _applied = (Math.Clamp(required - (baseline.Relative ? parentZ : 0),
            (int)RenderingServer.CanvasItemZMin, (int)RenderingServer.CanvasItemZMax), baseline.Relative);
        return _applied.Value;
    }
}

internal static class CreatureIntentDepth
{
    private static readonly ConditionalWeakTable<Control, CreatureIntentDepthState> States = new();

    // Called on intent changes (including phase transitions) and visual replacement,
    // never per frame. Include dormant particles so their later emission is safe too.
    internal static void Apply(NCreature creature)
    {
        try
        {
            if (creature.Entity?.IsMonster != true ||
                !GodotObject.IsInstanceValid(creature.Visuals) ||
                !GodotObject.IsInstanceValid(creature.IntentContainer)) return;
            var intent = creature.IntentContainer;
            var visuals = creature.Visuals;
            if (!intent.IsInsideTree() || !visuals.IsInsideTree() ||
                intent.GetCanvas() != visuals.GetCanvas()) return;

            var maximum = EffectiveZ(visuals);
            Node highest = visuals;
            var detached = !IsAfterVisualBranch(creature, intent, visuals) || intent.ShowBehindParent;
            var pending = new Stack<Node>();
            pending.Push(visuals);
            while (pending.TryPop(out var node))
            {
                // These are separate rendering surfaces, not this creature's draw order.
                if (node is CanvasLayer or Viewport) continue;
                if (node is CanvasItem item)
                {
                    var depth = EffectiveZ(item);
                    if (depth > maximum) { maximum = depth; highest = item; }
                    detached |= item.TopLevel;
                }
                foreach (var child in node.GetChildren()) pending.Push(child);
            }

            var parentZ = intent.TopLevel ? 0 : EffectiveZ(intent.GetParent() as CanvasItem);
            var previous = (intent.ZIndex, intent.ZAsRelative);
            var result = States.GetValue(intent, _ => new()).Resolve(
                previous.ZIndex, previous.ZAsRelative, parentZ, maximum, detached);
            if (result == previous) return;
            intent.ZIndex = result.Z;
            intent.ZAsRelative = result.Relative;
            ModLog.Info($"意图图层保护 monster={creature.Entity.Monster?.Id.Entry} " +
                $"visual={creature.GetPathTo(highest)} visualZ={maximum} " +
                $"intent={previous}->{result} parentZ={parentZ} position={intent.GlobalPosition}。");
        }
        catch (Exception exception)
        {
            // A cosmetic diagnostic/correction must never interrupt a combat action.
            ModLog.Warn("意图图层保护未应用：" + exception.GetBaseException().Message);
        }
    }

    private static int EffectiveZ(CanvasItem? item)
    {
        if (item == null) return 0;
        var parent = item.ZAsRelative && !item.TopLevel ? item.GetParent() as CanvasItem : null;
        return Math.Clamp(item.ZIndex + EffectiveZ(parent),
            (int)RenderingServer.CanvasItemZMin, (int)RenderingServer.CanvasItemZMax);
    }

    private static bool IsAfterVisualBranch(Node creature, Node intent, Node visuals)
    {
        static Node? Branch(Node owner, Node node)
        {
            while (node.GetParent() is { } parent)
            {
                if (parent == owner) return node;
                node = parent;
            }
            return null;
        }
        var intentBranch = Branch(creature, intent);
        var visualBranch = Branch(creature, visuals);
        return intentBranch != null && visualBranch != null &&
               intentBranch.GetIndex() > visualBranch.GetIndex();
    }
}

[HarmonyPatch(typeof(NCreature), nameof(NCreature.UpdateIntent))]
internal static class CreatureIntentDepthPatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(NCreature __instance) => CreatureIntentDepth.Apply(__instance);
}
