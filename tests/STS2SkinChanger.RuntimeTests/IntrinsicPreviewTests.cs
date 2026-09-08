using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using STS2SkinChanger;

internal static class IntrinsicPreviewTests
{
    private static readonly List<MethodBase> Replayed = [];
    private static Type Bridge => typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.IntrinsicCharacterPreview")
        ?? throw new InvalidOperationException("新角色的预览未衔接角色自身的模型就绪补丁。");

    public static void Run()
    {
        var allows = AccessTools.Method(Bridge, "CanReplay");
        bool Check(string name) => (bool)allows.Invoke(null,
            [AccessTools.Method(typeof(IntrinsicPreviewTests), name), typeof(IntrinsicPreviewTests).Assembly])!;
        Require(Check(nameof(VisualPostfix)), "必须沿延迟回调识别本角色模型初始化，不能因为模型先返回原皮底座就跳过。");
        Require(!Check(nameof(CounterPostfix)), "不能把只添加战斗计数器的就绪补丁带进模型预览。");
        Require(!Check(nameof(MixedPostfix)), "即便操作模型，也不能执行改变战斗状态的混合补丁。");
        Require(!(bool)allows.Invoke(null, [AccessTools.Method(typeof(IntrinsicPreviewTests), nameof(VisualPostfix)), typeof(Entry).Assembly])!,
            "不能执行另一角色程序集的就绪补丁。");
        Console.WriteLine("Intrinsic preview policy passed: deferred visual setup, owner isolation and gameplay/UI exclusion.");
    }

    public static void Audit(string path)
    {
        var mod = Assembly.LoadFrom(Path.GetFullPath(path));
        var allows = AccessTools.Method(Bridge, "CanReplay");
        var candidates = mod.GetTypes().Where(type => type.Name == "CreatureReadyPatch")
            .SelectMany(type => type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(method => method.Name == "Postfix" && method.GetParameters() is [{ ParameterType: var t }] && t == typeof(NCreature))
            .ToArray();
        var accepted = candidates.Where(method => (bool)allows.Invoke(null, [method, mod])!).ToArray();
        foreach (var callback in candidates)
        {
            Console.WriteLine($"{mod.GetName().Name}: {callback.DeclaringType!.FullName}: replay={accepted.Contains(callback)}");
            if (!accepted.Contains(callback)) ExplainRejectedCalls(callback, mod);
        }
        Require(accepted.Length == 1, "实包必须且只能接入一条模型初始化，不执行其它就绪逻辑：" + mod.GetName().Name);
        Require(accepted[0].DeclaringType!.DeclaringType?.Name.EndsWith("VisualPatch") == true,
            "实包接入的必须是已审计的模型替换补丁。");
        var characterType = mod.GetTypes().Single(type => type.Name == mod.GetName().Name + "Character");
        var character = (CharacterModel)RuntimeHelpers.GetUninitializedObject(characterType);
        AccessTools.Field(typeof(AbstractModel), "<Id>k__BackingField")
            .SetValue(character, new ModelId("CHARACTER", "AUDIT_CHARACTER"));
        var owner = (NCreature)RuntimeHelpers.GetUninitializedObject(typeof(NCreature));
        var harmony = new Harmony("tests.intrinsic-preview-routing");
        try
        {
            harmony.Patch(AccessTools.Method(typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.ModLog"), "Info"),
                prefix: new HarmonyMethod(typeof(IntrinsicPreviewTests), nameof(LogPrefix)));
            foreach (var callback in candidates)
            {
                // Keep the actual callback IL for classification. Replace only its native
                // execution boundary; no foreign initializer, resource or combat hook is run.
                harmony.Patch(callback, prefix: new HarmonyMethod(typeof(IntrinsicPreviewTests), nameof(ObserveReplay)));
                harmony.Patch(AccessTools.Method(typeof(NCreature), "_Ready"), postfix: new HarmonyMethod(callback));
            }
            Replayed.Clear();
            Require((bool)AccessTools.Method(Bridge, "Replay").Invoke(null, [owner, character, null])!,
                "真实角色类型的模型就绪回调必须进入预览路由。");
            Require(Replayed.Count == 1 && Replayed[0] == accepted[0],
                "预览路由只能调用本角色已安装的模型步骤，不能执行计数器或完整 Ready。");
            Console.WriteLine("  actual installed-patch routing passed without running native callbacks");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }

    private static bool ObserveReplay(MethodBase __originalMethod)
    {
        Replayed.Add(__originalMethod);
        return false;
    }

    private static bool LogPrefix(string __0) { Console.WriteLine(__0); return false; }

    private static void ExplainRejectedCalls(MethodBase root, Assembly mod)
    {
        var allow = AccessTools.Method(Bridge, "AllowsExternalCall");
        var pending = new Stack<MethodBase>();
        var visited = new HashSet<MethodBase>();
        pending.Push(root);
        while (pending.TryPop(out var method) && visited.Count < 128)
        {
            if (!visited.Add(method)) continue;
            try
            {
                foreach (var instruction in PatchProcessor.GetOriginalInstructions(method))
                {
                    if (instruction.operand is not MethodBase called) continue;
                    if (called.DeclaringType?.Assembly == mod) pending.Push(called);
                    else if (!(bool)allow.Invoke(null, [called, method])!)
                        Console.WriteLine($"  skipped external call {method.DeclaringType?.Name}.{method.Name} -> {called.DeclaringType?.FullName}.{called.Name}");
                }
            }
            catch (Exception e) { Console.WriteLine($"  unreadable {method}: {e.GetBaseException().Message}"); }
        }
    }

    private static void VisualPostfix(NCreature __instance)
        => Callable.From(() => AttachVisual(__instance)).CallDeferred();

    private static void AttachVisual(NCreature owner)
    {
        if (owner.Body is { } body) body.Visible = false;
        owner.Visuals.AddChild(new Node2D());
    }

    private static void CounterPostfix(NCreature __instance)
        => __instance.AddChild(new Label());

    private static void MixedPostfix(NCreature __instance)
    {
        AttachVisual(__instance);
        __instance.Entity.SetCurrentHpInternal(1);
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
