using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Nodes.Combat;
using STS2SkinChanger;

internal static class CreatureVisualLifecycleTests
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static Node _owner = null!;
    private static Node _oldParent = null!;
    private static Node _oldVisuals = null!;
    private static NCreatureVisuals _newVisuals = null!;
    private static FieldInfo _visualsField = null!;
    private static bool _detached;
    private static bool _attached;
    private static int _finishingCalls;

    internal static void Run()
    {
        CheckCreaturePostfixDiscovery();
        CheckNativeReadyParent();
        CheckLocalCreatureFinishing();
        CheckCompanionRevealEligibility();
        Console.WriteLine("Creature visual lifecycle passed: selected Creature postfixes and native Ready parent.");
    }

    private static void CheckCompanionRevealEligibility()
    {
        var loader = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.ManagedSkinModLoader", true)!;
        var eligible = loader.GetMethod("IsCompanionRevealPresentationPatch", Static)
            ?? throw new InvalidOperationException("热替换活着的随从必须补齐被登场流程隐藏后的显示初始化。");
        var patchType = loader.GetNestedType("ProviderPatch", BindingFlags.NonPublic)!;
        var kindType = loader.GetNestedType("ProviderPatchKind", BindingFlags.NonPublic)!;
        var callback = typeof(CreatureFinishingPatch).GetMethod("Postfix", Static)!;
        foreach (var (method, kind, pet, dead, hidden, expected) in new[]
        {
            ("StartReviveAnim", "Prefix", true, false, true, true),
            ("StartReviveAnim", "Prefix", false, false, true, false),
            ("StartReviveAnim", "Prefix", true, true, true, false),
            ("StartReviveAnim", "Prefix", true, false, false, false),
            ("StartReviveAnim", "Postfix", true, false, true, false),
            ("_Ready", "Prefix", true, false, true, false)
        })
        {
            var patch = Activator.CreateInstance(patchType,
                AccessTools.Method(typeof(NCreature), method), callback, Enum.Parse(kindType, kind),
                Priority.Normal, Array.Empty<string>(), Array.Empty<string>(), false)!;
            Require((bool)eligible.Invoke(null, [patch, pet, dead, hidden])! == expected,
                "随从显示补全只能用于活着且主模型被隐藏的随从，不得复活死亡单位或重复执行正常登场。");
        }
    }

    private static void CheckLocalCreatureFinishing()
    {
        var assembly = typeof(Entry).Assembly;
        var controls = assembly.GetType("STS2SkinChanger.Ui.ContextualSkinControls", true)!;
        var finish = controls.GetMethod("ApplySelectedCreatureVisualPostfix", Static)!;
        var patch = assembly.GetType("STS2SkinChanger.Core.MultiplayerCreatureVisualScopePatch", true)!;
        var postfix = patch.GetMethod("Postfix", Static)!;
        var harmony = new Harmony("Gurio.SkinChanger.Tests.LocalCreatureFinishing");
        try
        {
            _finishingCalls = 0;
            harmony.Patch(finish, prefix: new HarmonyMethod(typeof(CreatureVisualLifecycleTests), nameof(CaptureFinishing)));
            var creature = RuntimeHelpers.GetUninitializedObject(typeof(Creature));
            postfix.Invoke(null, [creature, _newVisuals, null]);
            Require(_finishingCalls == 1, "没有多人资源范围的本机生物也必须且只能执行一次选中皮肤的生物级收尾。");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
        }
    }

    private static bool CaptureFinishing(Creature creature, ref NCreatureVisuals? visuals)
    {
        Require(ReferenceEquals(visuals, _newVisuals), "收尾必须操作最终模型，不能又从原版工厂创建一个。");
        _finishingCalls++;
        return false;
    }

    private static void CheckCreaturePostfixDiscovery()
    {
        var loader = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.ManagedSkinModLoader", true)!;
        var discover = loader.GetMethod("DiscoverVisualPostfixes", Static)!;
        var discovered = ((System.Collections.IEnumerable)discover.Invoke(null, [typeof(CreatureVisualLifecycleTests).Assembly])!)
            .Cast<object>().ToArray();
        var callback = typeof(CreatureFinishingPatch).GetMethod("Postfix", Static)!;
        Require(discovered.Any(item => Equals(item.GetType().GetProperty("Method")!.GetValue(item), callback)),
            "Creature.CreateVisuals 的收尾必须被发现，否则删除鬼火等操作永远不会重放。");
        var patchType = loader.GetNestedType("ProviderPatch", BindingFlags.NonPublic)!;
        var kindType = loader.GetNestedType("ProviderPatchKind", BindingFlags.NonPublic)!;
        var target = AccessTools.Method(typeof(Creature), nameof(Creature.CreateVisuals));
        var ownership = loader.GetMethod("IsManagedResourceOwnershipPatch", Static)!;
        foreach (var kind in new[] { "Postfix", "Prefix" })
        {
            var patch = Activator.CreateInstance(patchType,
                target, callback, Enum.Parse(kindType, kind), Priority.Normal,
                Array.Empty<string>(), Array.Empty<string>(), false)!;
            Require((bool)ownership.Invoke(null, [patch])! == (kind == "Postfix"),
                "可重放的 Creature 收尾必须停止全局执行，且不能误停不支持重放的前置行为。");
        }
        var stateful = Activator.CreateInstance(patchType, target,
            typeof(StatefulCreaturePatch).GetMethod("Postfix", Static)!, Enum.Parse(kindType, "Postfix"),
            Priority.Normal, Array.Empty<string>(), Array.Empty<string>(), false)!;
        Require(!(bool)ownership.Invoke(null, [stateful])!,
            "需要原生前置 __state 的回调不可脱离原调用重放，不能误停后丢失它的效果。");
    }

    private static void CheckNativeReadyParent()
    {
        var runtime = typeof(Entry).Assembly.GetType("STS2SkinChanger.Ui.CharacterAppearanceRuntime", true)!;
        var attach = runtime.GetMethod("AttachReplacementCreatureVisuals", Static)
            ?? throw new InvalidOperationException("热切换必须以原生生物节点为父级初始化新外观，不能在位移容器下触发 Ready。");
        _owner = (NCreature)RuntimeHelpers.GetUninitializedObject(typeof(NCreature));
        _oldParent = (Node2D)RuntimeHelpers.GetUninitializedObject(typeof(Node2D));
        _oldVisuals = (NCreatureVisuals)RuntimeHelpers.GetUninitializedObject(typeof(NCreatureVisuals));
        _newVisuals = (NCreatureVisuals)RuntimeHelpers.GetUninitializedObject(typeof(NCreatureVisuals));
        _visualsField = (FieldInfo)runtime.GetField("VisualsField", Static)!.GetValue(null)!;
        _visualsField.SetValue(_owner, _oldVisuals);
        _attached = _detached = false;

        // Only replace the two native engine calls. Run the actual attachment code, checking
        // what the provider's Ready callback observes at the AddChild boundary.
        var harmony = new Harmony("Gurio.SkinChanger.Tests.CreatureAttachment");
        try
        {
            harmony.Patch(AccessTools.Method(typeof(Node), nameof(Node.RemoveChild)),
                prefix: new HarmonyMethod(typeof(CreatureVisualLifecycleTests), nameof(RemoveChild)));
            harmony.Patch(AccessTools.Method(typeof(Node), nameof(Node.AddChild)),
                prefix: new HarmonyMethod(typeof(CreatureVisualLifecycleTests), nameof(AddChild)));
            attach.Invoke(null, [_owner, _oldParent, _oldVisuals, _newVisuals]);
            Require(_attached && _detached, "新模型必须替换旧模型并经历原生父级下的初始化。");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
        }
    }

    private static bool RemoveChild(Node __instance, Node node)
    {
        Require(ReferenceEquals(__instance, _oldParent) && ReferenceEquals(node, _oldVisuals),
            "只允许移出被替换的旧模型。");
        _detached = true;
        return false;
    }

    private static bool AddChild(Node __instance, Node node)
    {
        Require(_detached && ReferenceEquals(__instance, _owner) && ReferenceEquals(node, _newVisuals),
            "原作者 Ready 必须看到 NCreature 父级，且不能再按旧节点名找到旧模型。");
        Require(ReferenceEquals(_visualsField.GetValue(_owner), _newVisuals),
            "Ready 读取 NCreature.Visuals 时必须已经指向新模型。");
        _attached = true;
        return false;
    }

    [HarmonyPatch(typeof(Creature), nameof(Creature.CreateVisuals))]
    private static class CreatureFinishingPatch
    {
        private static void Postfix(Creature __instance, ref NCreatureVisuals __result) { }
    }

    [HarmonyPatch(typeof(Creature), nameof(Creature.CreateVisuals))]
    private static class StatefulCreaturePatch
    {
        private static void Postfix(Creature __instance, bool __state, ref NCreatureVisuals __result) { }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
