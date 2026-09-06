using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Rooms;
using STS2SkinChanger;

public static class CombatHudLifecycleTests
{
    private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly List<string> Events = [];
    private static readonly HudRegistry Registry = new();
    private static NCombatUi _ui = null!;
    private static CombatState _combat = null!;
    private static bool _replaceThrows;
    private static CombatManager _manager = null!;
    private static bool _exitThrows;

    internal static void Run()
    {
        var runtime = typeof(Entry).Assembly.GetType("STS2SkinChanger.Ui.CharacterAppearanceRuntime", true)!;
        var refresh = runtime.GetMethod("RefreshEnergyCounter", Static)
            ?? throw new InvalidOperationException("换肤释放能量控件前缺少 HUD 生命周期交接入口。");
        var core = runtime.GetMethod("ReplaceEnergyCounterCore", Static)!;
        var harmony = new Harmony("tests.combat-hud-lifecycle");
        InstallHudFixture();
        try
        {
            // Replace only engine tree mutation. Exercise the actual production entry, discovery,
            // provider registry, restore/unsubscribe and attach ordering around that mutation.
            harmony.Patch(core, prefix: new HarmonyMethod(typeof(CombatHudLifecycleTests), nameof(ReplaceNativeCounter)));
            harmony.Patch(typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.ModLog", true)!.GetMethod("Warn", Static)!,
                prefix: new HarmonyMethod(typeof(CombatHudLifecycleTests), nameof(SkipLog)));
            foreach (var fail in new[] { false, true })
            {
                Reset();
                _replaceThrows = fail;
                var state = new HudState();
                Registry.State = state;
                refresh.Invoke(null, [_ui, Uninitialized<Player>()]);
                Require(Events.SequenceEqual(new[] { "restore", "unsubscribe", "replace", "attach" }),
                    "必须在释放旧控件前恢复/解绑，并在成功或回滚后重新绑定原 HUD。");
                Require(ReferenceEquals(Registry.State, state) && state.Subscriptions == 1,
                    "不能重复创建 CZN HUD，或留下重复/丢失的绑定。");
                state.RestoreNativeResourceLayout(); // Old cached controls would throw here.
            }

            Reset();
            Registry.State = null;
            refresh.Invoke(null, [_ui, Uninitialized<Player>()]);
            Require(Events.SequenceEqual(new[] { "replace" }), "没有启用 CZN HUD 时不可替玩家启用或创建它。");

            Reset();
            Registry.State = new HudState { FailRestore = true };
            refresh.Invoke(null, [_ui, Uninitialized<Player>()]);
            Require(!Events.Contains("replace"), "作者 HUD 无法安全解绑时，必须保留原计数器，不能继续释放它。");

            CheckFailedExitStillDisconnects(harmony);
        }
        finally { harmony.UnpatchAll(harmony.Id); Registry.State = null; }
        Console.WriteLine("Combat HUD lifecycle passed: rebind, rollback, disabled HUD, failed detach, failed exit cleanup.");
    }

    private static void Reset()
    {
        Events.Clear();
        _replaceThrows = false;
        _ui = Uninitialized<NCombatUi>();
        _combat = Uninitialized<CombatState>();
        AccessTools.FieldRefAccess<NCombatUi, CombatState>("_state")(_ui) = _combat;
        Registry.Owner = _ui;
    }

    private static bool ReplaceNativeCounter(NCombatUi combatUi, Player player)
    {
        Require(ReferenceEquals(combatUi, _ui), "不得替换另一个战斗界面的控件。");
        Events.Add("replace");
        Registry.State?.InvalidateOldCounter();
        if (_replaceThrows) throw new InvalidOperationException("simulated native replacement failure");
        return false;
    }

    // The fixture models the audited CZN contract, not Godot rendering. It deliberately throws
    // when a retained visibility snapshot is restored after SC has destroyed its old control.
    public sealed class HudState
    {
        public bool FailRestore;
        private bool _holdingOld = true;
        private bool _oldFreed;
        public int Subscriptions = 1;
        public void RestoreNativeResourceLayout()
        {
            Events.Add("restore");
            if (FailRestore || (_holdingOld && _oldFreed)) throw new ObjectDisposedException("Godot.Control");
            _holdingOld = false;
        }
        public void Unsubscribe() { Events.Add("unsubscribe"); Subscriptions = 0; }
        public void InvalidateOldCounter() { _oldFreed = true; }
        public void Bind() { _holdingOld = true; _oldFreed = false; Subscriptions = 1; }
    }

    public sealed class HudRegistry
    {
        public NCombatUi Owner = null!;
        public HudState? State;
        internal bool TryGetValue(NCombatUi owner, out HudState? state)
        {
            Require(ReferenceEquals(owner, Owner), "HUD 查询必须按当前 NCombatUi 隔离。");
            state = State;
            return state != null;
        }
    }

    public static void Attach(NCombatUi ui, CombatState combat)
    {
        Require(ReferenceEquals(ui, _ui) && ReferenceEquals(combat, _combat), "HUD 重新绑定必须保留原战斗状态。");
        Events.Add("attach");
        Registry.State!.Bind();
    }

    private static void InstallHudFixture()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("CznStyleUI"), AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("Fixture");
        var type = module.DefineType("CznStyleUI.CombatHud.CombatResourceHudRuntime", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        type.DefineField("States", typeof(HudRegistry), FieldAttributes.Static | FieldAttributes.Private);
        var attach = type.DefineMethod("Attach", MethodAttributes.Public | MethodAttributes.Static,
            typeof(void), [typeof(NCombatUi), typeof(CombatState)]);
        var il = attach.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, typeof(CombatHudLifecycleTests).GetMethod(nameof(Attach))!); il.Emit(OpCodes.Ret);
        type.CreateType()!.GetField("States", Static)!.SetValue(null, Registry);
    }

    private static void CheckFailedExitStillDisconnects(Harmony harmony)
    {
        var patch = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.CombatUiExitCleanupPatch")
            ?? throw new InvalidOperationException("扩展退出回调抛错时，缺少原版战斗订阅清理保护。");
        harmony.CreateClassProcessor(patch).Patch();
        _manager = Uninitialized<CombatManager>();
        harmony.Patch(AccessTools.PropertyGetter(typeof(CombatManager), "Instance"),
            prefix: new HarmonyMethod(typeof(CombatHudLifecycleTests), nameof(GetManager)));
        var exit = AccessTools.Method(typeof(NCombatUi), "_ExitTree");
        harmony.Patch(exit, prefix: new HarmonyMethod(typeof(CombatHudLifecycleTests), nameof(ThrowHudExit)));
        foreach (var failure in new[] { "prefix", "prefix-and-cancellation", "none" })
        {
            var ui = Uninitialized<NCombatUi>();
            using var cts = new CancellationTokenSource();
            using var registration = cts.Token.Register(() =>
            {
                if (failure == "prefix-and-cancellation") throw new InvalidOperationException("cancel callback failure");
            });
            AccessTools.FieldRefAccess<NCombatUi, CancellationTokenSource>("_cts")(ui) = cts;
            var won = AccessTools.Method(typeof(NCombatUi), "OnCombatWon").CreateDelegate<Action<CombatRoom>>(ui);
            var ended = AccessTools.Method(typeof(NCombatUi), "OnCombatEnded").CreateDelegate<Action<CombatRoom>>(ui);
            _manager.CombatWon += won;
            _manager.CombatEnded += ended;
            _exitThrows = failure != "none";
            try
            {
                exit.Invoke(ui, null);
                Require(!_exitThrows, "不应悄悄吞掉作者退出异常。");
            }
            catch (TargetInvocationException exception) when (_exitThrows && exception.InnerException is ObjectDisposedException) { }
            Require(cts.IsCancellationRequested, "退出补丁抛错后仍须取消原版界面的待执行任务。");
            foreach (var eventName in new[] { "CombatWon", "CombatEnded" })
            {
                var handlers = (Delegate?)AccessTools.Field(typeof(CombatManager), eventName).GetValue(_manager);
                Require(handlers == null || handlers.GetInvocationList().All(d => !ReferenceEquals(d.Target, ui)),
                    "已退出的战斗界面仍留有回调，会在下一场战斗重复生成奖励。");
            }
        }
    }

    internal static void Audit(string path)
    {
        // Inspect the shipped assembly without running its initializer, HUD constructors or
        // native rendering. This catches private-contract changes hidden by the small fixture.
        var assembly = Assembly.LoadFrom(Path.GetFullPath(path));
        var runtime = assembly.GetType("CznStyleUI.CombatHud.CombatResourceHudRuntime", true)!;
        var lookup = runtime.GetField("States", Static)!.FieldType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(m => m.Name == "TryGetValue" && m.GetParameters() is [{ ParameterType: var owner }, { IsOut: true }] &&
                         owner == typeof(NCombatUi));
        var state = lookup.GetParameters()[1].ParameterType.GetElementType()!;
        foreach (var methodName in new[] { "RestoreNativeResourceLayout", "Unsubscribe" })
            Require(AccessTools.Method(state, methodName, Type.EmptyTypes)?.ReturnType == typeof(void),
                "CZN HUD 旧控件交接接口已改变：" + methodName);
        Require(AccessTools.Method(runtime, "Attach", [typeof(NCombatUi), typeof(CombatState)])?.ReturnType == typeof(void),
            "CZN HUD 必须能按原战斗状态重新绑定，不能回放 NCombatUi.Activate。");
        var bind = AccessTools.Method(state, "Bind");
        var instructions = PatchProcessor.GetOriginalInstructions(bind);
        Require(instructions.Any(i => i.operand is MethodInfo m && m.Name == "CaptureNativeResourceLayout"),
            "CZN Bind 必须重新捕获替换后的原生控件，而不是继续保留旧控件快照。");
        Console.WriteLine("Actual CZN HUD contract passed: registry, restore/unsubscribe and existing-state rebind.");
    }

    private static bool GetManager(ref CombatManager __result) { __result = _manager; return false; }
    private static void ThrowHudExit() { if (_exitThrows) throw new ObjectDisposedException("Godot.Control"); }
    private static bool SkipLog() => false;
    private static T Uninitialized<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
