using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace STS2SkinChanger.Core;

/// <summary>
/// Cooperates with the already loaded HUD owner before replacing native energy controls.
/// Never replays NCombatUi.Activate: that would subscribe combat/reward callbacks twice.
/// </summary>
internal static class CombatHudResourceLifecycle
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string RuntimeName = "CznStyleUI.CombatHud.CombatResourceHudRuntime";

    internal static IDisposable? BeforeEnergyCounterReplacement(NCombatUi ui)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate =>
            candidate.GetName().Name == "CznStyleUI");
        if (assembly == null) return null;

        var runtime = assembly.GetType(RuntimeName)
            ?? throw new MissingMemberException("CZN HUD 接口已变化，已保留原能量控件。");
        var statesField = runtime.GetField("States", Static)
            ?? throw new MissingFieldException(RuntimeName, "States");
        var lookup = statesField.FieldType.GetMethods(Instance).SingleOrDefault(method =>
            method.Name == "TryGetValue" && method.ReturnType == typeof(bool) &&
            method.GetParameters() is [{ ParameterType: var owner }, { IsOut: true }] &&
            owner == typeof(NCombatUi))
            ?? throw new MissingMethodException(RuntimeName, "States.TryGetValue");
        var states = statesField.GetValue(null);
        object?[] lookupArguments = [ui, null];
        if (states == null || !(bool)lookup.Invoke(states, lookupArguments)! || lookupArguments[1] == null)
            return null; // Do not activate a HUD that the player has not enabled.

        var state = lookupArguments[1]!;
        var restore = RequiredMethod(state.GetType(), "RestoreNativeResourceLayout", Instance, []);
        var unsubscribe = RequiredMethod(state.GetType(), "Unsubscribe", Instance, []);
        var attach = RequiredMethod(runtime, "Attach", Static, [typeof(NCombatUi), typeof(CombatState)]);
        var combat = AccessTools.Field(typeof(NCombatUi), "_state")?.GetValue(ui) as CombatState
            ?? throw new InvalidOperationException("当前能量界面没有可重新绑定的战斗状态，已保留原控件。");

        // CZN Detach is an exit-only API: it disposes its state without deleting its root.
        // Use the existing state instead, restoring/releasing its old references while they
        // are alive. Attach then Bind recaptures the new counter without creating another HUD.
        var binding = new HudBinding(attach, ui, combat);
        try
        {
            restore.Invoke(state, null);
            unsubscribe.Invoke(state, null);
            return binding;
        }
        catch
        {
            binding.Dispose(); // Restore bindings after a partially failed handover; no free yet.
            throw;
        }
    }

    private static MethodInfo RequiredMethod(Type type, string name, BindingFlags flags, Type[] arguments)
    {
        var method = type.GetMethod(name, flags, null, arguments, null);
        return method?.ReturnType == typeof(void) ? method : throw new MissingMethodException(type.FullName, name);
    }

    private sealed class HudBinding(MethodInfo attach, NCombatUi ui, CombatState state) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { attach.Invoke(null, [ui, state]); }
            catch (Exception exception)
            {
                ModLog.Warn("重新绑定 CZN 能量界面失败：" + exception.GetBaseException().Message);
            }
        }
    }
}

/// <summary>
/// A failing UI mod prefix must not strand the game's subscriptions on a freed combat UI.
/// Retain the exception for Godot's log; finish only the native cancellation/unsubscription.
/// </summary>
[HarmonyPatch(typeof(NCombatUi), nameof(NCombatUi._ExitTree))]
internal static class CombatUiExitCleanupPatch
{
    [HarmonyFinalizer]
    private static Exception? Finalizer(NCombatUi __instance, Exception? __exception)
    {
        if (__exception == null) return null;
        try
        {
            (AccessTools.Field(typeof(NCombatUi), "_cts")?.GetValue(__instance) as CancellationTokenSource)?.Cancel();
        }
        catch (Exception exception)
        {
            ModLog.Warn("战斗界面退出异常后的任务取消失败：" + exception.GetBaseException().Message);
        }
        try
        {
            AccessTools.Method(typeof(NCombatUi), "DisconnectSignals")!.Invoke(__instance, null);
        }
        catch (Exception exception)
        {
            ModLog.Warn("战斗界面退出异常后的回调清理失败：" + exception.GetBaseException().Message);
        }
        ModLog.Warn("战斗界面扩展退出异常，已尝试补做原版任务取消和战斗回调清理，原始异常仍保留。");
        return __exception;
    }
}
