using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using STS2SkinChanger;

internal static class OptionalCharacterVfxTests
{
    internal static void Run()
    {
        var harmony = new Harmony("tests.optional-character-vfx");
        try
        {
            foreach (var name in new[] { "NecrobinderOptionalHeadReadyPatch", "NecrobinderOptionalHeadAnimationPatch" })
                if (typeof(Entry).Assembly.GetType("STS2SkinChanger.Core." + name) is { } patch)
                    harmony.CreateClassProcessor(patch).Patch();
            var vfx = (NNecrobinderVfx)RuntimeHelpers.GetUninitializedObject(typeof(NNecrobinderVfx));
            // Providers may remove the native head/ghost-fire attachment, before Ready or later.
            // The real animation callback must not dereference it or touch a native animation state.
            try { AccessTools.Method(typeof(NNecrobinderVfx), "UpdateFlameVisibility").Invoke(vfx, [null, null, null]); }
            catch (TargetInvocationException exception)
            {
                throw new InvalidOperationException("皮肤移除鬼火节点后，原版动画回调仍抛异常。", exception.InnerException);
            }
            // Reflection SetValue forces beta's Godot StringName static initializer, which
            // requires an engine. Direct instance-field access tests the freed-node path without it.
            AccessTools.FieldRefAccess<NNecrobinderVfx, Node2D>("_headRef")(vfx) =
                (Node2D)RuntimeHelpers.GetUninitializedObject(typeof(Node2D));
            AccessTools.Method(typeof(NNecrobinderVfx), "UpdateFlameVisibility").Invoke(vfx, [null, null, null]);

            // Check the actual patched Ready body on both game binaries: absence is optional,
            // while all particle initialization and animation connections remain original.
            var ready = AccessTools.Method(typeof(NNecrobinderVfx), "_Ready");
            var instructions = PatchProcessor.GetCurrentInstructions(ready);
            Require(!instructions.Any(i => i.operand is MethodInfo method && method.DeclaringType == typeof(Node) &&
                    method.Name == "GetNode"), "Ready 仍通过必需节点查找访问已删除的 HeadBoneNode。");
            var original = PatchProcessor.GetOriginalInstructions(ready);
            Require(instructions.Count(i => i.operand is MethodInfo method && method.DeclaringType == typeof(Node) &&
                    method.Name == "GetNodeOrNull") == 1 + original.Count(i => i.operand is MethodInfo method &&
                    method.DeclaringType == typeof(Node) && method.Name == "GetNodeOrNull"),
                "只应将头部改为可选；各版本其它的粒子查找应原样保留。");
            Require(instructions.Any(i => i.operand is MethodInfo method && method.Name == "ConnectAnimationEvent"),
                "不能为停止鬼火异常禁用其它正常的镰刀动画特效。");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
        Console.WriteLine("Optional character VFX passed: absent/freed head, native particle/event lifecycle retained.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
