using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Combat;
using STS2SkinChanger;

internal static class AppearanceRevivalTests
{
    internal static void Run()
    {
        var runtime = typeof(Entry).Assembly.GetType("STS2SkinChanger.Ui.CharacterAppearanceRuntime", true)!;
        var active = AccessTools.Method(runtime, "HasActiveDeathAnimation")
            ?? throw new InvalidOperationException("外观不能把已完成的死亡任务当成正在播放。");
        var creature = (NCreature)RuntimeHelpers.GetUninitializedObject(typeof(NCreature));
        bool Blocked() => (bool)active.Invoke(null, [creature])!;
        Require(!Blocked(), "从未死亡的单位不应被阻止。");
        var death = new TaskCompletionSource();
        creature.DeathAnimationTask = death.Task;
        Require(Blocked(), "正在播放死亡动画时仍须等候，不能替换动画使用中的节点。");
        death.SetResult();
        Require(creature.IsPlayingDeathAnimation && !Blocked(), "原生标记仍为真；完成后待复活和复活单位都必须可调整。");
        creature.DeathAnimationTask = Task.FromCanceled(new CancellationToken(true));
        Require(!Blocked(), "已取消的死亡任务不能永久锁住外观。");
        creature.DeathAnimationTask = Task.FromException(new InvalidOperationException("fixture"));
        _ = creature.DeathAnimationTask.Exception;
        Require(!Blocked(), "结束于异常的任务不能永久锁住外观。");
        foreach (var name in new[] { "CanApplySelectionNow", "RefreshLiveCreatures", "RefreshPlayerAppearance", "RefreshPlayerTransforms" })
        {
            // LINQ predicates can contain the guard in a compiler-generated method.
            var methods = runtime.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .Concat(runtime.GetNestedTypes(BindingFlags.NonPublic).SelectMany(t => t.GetMethods(
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)))
                .Where(m => m.Name == name || m.Name.Contains("<" + name + ">"));
            var called = methods.SelectMany(m => PatchProcessor.GetOriginalInstructions(m))
                .Select(i => i.operand).OfType<MethodInfo>().ToArray();
            Require(called.Any(m => m == active) && called.All(m => m.Name != "get_IsPlayingDeathAnimation"),
                "所有外观刷新入口必须使用任务完成状态：" + name);
        }
        Require(PatchProcessor.GetOriginalInstructions(AccessTools.Method(runtime, "TryRebuildCreatureVisuals"))
                .Any(i => i.operand is MethodInfo m && m.Name == "RestoreDeadCreaturePose"),
            "待复活时热换模型必须恢复死亡姿态，不能直接站起来。");
        Console.WriteLine("Appearance revival passed: live, dying, waiting, revived and canceled/faulted death tasks.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
