using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using STS2SkinChanger;

internal static class SceneAppearanceLifecycleTests
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static void Run()
    {
        CheckOutgoingCombatCannotOwnIncomingShop();
        CheckHealthAwareIdle();
        CheckUninitializedRunRoom();
        Console.WriteLine("Scene appearance lifecycle passed: shop ownership and health-aware idle.");
    }

    private static void CheckOutgoingCombatCannotOwnIncomingShop()
    {
        var assembly = typeof(Entry).Assembly;
        var runtime = assembly.GetType("STS2SkinChanger.Ui.CharacterAppearanceRuntime", true)!;
        var service = assembly.GetType("STS2SkinChanger.Core.SkinService", true)!;
        var active = runtime.GetField("_combatRuntimeScopeActive", Static)!;
        var lease = runtime.GetField("_combatRuntimeScopeLease", Static)!;
        var scope = service.GetField("_runtimeProviderBehaviorScope", Static)!;
        var focus = service.GetMethod("FocusRuntimeProviderBehaviorsOnGroups", Static)!;
        var savedScope = scope.GetValue(null);
        var savedActive = active.GetValue(null);
        var savedLease = lease.GetValue(null);
        try
        {
            // No catalog/native scene is needed: use the real service's ownership state machine.
            var combatLease = (long)focus.Invoke(null,
                [new[] { "character:silent", "monster:test" }, Array.Empty<string>(), "test combat"])!;
            active.SetValue(null, true);
            lease.SetValue(null, combatLease);
            var merchantPrefix = assembly.GetType("STS2SkinChanger.Ui.MerchantRoomCreateAppearancePatch", true)!
                .GetMethod("Prefix", Static)!;
            merchantPrefix.Invoke(null, null);
            var shopScope = scope.GetValue(null);
            Require(!ReferenceEquals(savedScope, shopScope) && (long)lease.GetValue(null)! == combatLease,
                "商店创建时不能把新商店的清理权限交给尚未退出的战斗房间。");
            runtime.GetMethod("ClearCombatRuntimeGroups", Static)!.Invoke(null, null);
            Require(ReferenceEquals(shopScope, scope.GetValue(null)),
                "旧战斗退出不得覆盖新商店范围，否则商人原 Mod 的 Ready 布局补丁会被停用。");

            // A compendium preview during combat may already have requested exactly the incoming
            // shop's providers. Equal resource sets must not imply equal lifecycle ownership.
            combatLease = (long)focus.Invoke(null,
                [new[] { "merchant" }, Array.Empty<string>(), "test preview"])!;
            active.SetValue(null, true);
            lease.SetValue(null, combatLease);
            merchantPrefix.Invoke(null, null);
            shopScope = scope.GetValue(null);
            runtime.GetMethod("ClearCombatRuntimeGroups", Static)!.Invoke(null, null);
            Require(ReferenceEquals(shopScope, scope.GetValue(null)),
                "新旧场景使用相同皮肤分组时也必须移交清理权限，不能复用旧房间的所有权。");

            active.SetValue(null, true);
            lease.SetValue(null, 0L);
            var focusContext = runtime.GetMethod("FocusRuntimeProviderBehaviorsOnRunContext", Static)!;
            var arguments = focusContext.GetParameters().Select(parameter => parameter.DefaultValue).ToArray();
            arguments[0] = new[] { "monster:test-refreshed" };
            var refreshParameter = Array.FindIndex(focusContext.GetParameters(), parameter => parameter.Name == "refreshCurrentRoom");
            Require(refreshParameter >= 0, "当前战斗刷新必须明确保留战斗的清理权限。");
            arguments[refreshParameter] = true;
            var refreshedLease = (long)focusContext.Invoke(null, arguments)!;
            Require(refreshedLease != 0 && (long)lease.GetValue(null)! == refreshedLease,
                "局内切肤或图鉴优先级刷新后，战斗仍须持有当前范围，以便正常退出时清理。");
            var combatScope = scope.GetValue(null);
            runtime.GetMethod("ClearCombatRuntimeGroups", Static)!.Invoke(null, null);
            Require(!ReferenceEquals(combatScope, scope.GetValue(null)),
                "不能以禁止所有退出清理来规避商人问题；未被新房间接管的战斗应照常清理。");
        }
        finally
        {
            active.SetValue(null, savedActive);
            lease.SetValue(null, savedLease);
            scope.SetValue(null, savedScope);
        }
    }

    private static void CheckUninitializedRunRoom()
    {
        var runtime = typeof(Entry).Assembly.GetType("STS2SkinChanger.Ui.CharacterAppearanceRuntime", true)!;
        var resolve = runtime.GetMethod("TryGetActiveCombatRoom", Static)
            ?? throw new InvalidOperationException("读取战斗房间前必须检查 NRun 房间容器已初始化。");
        var uninitializedRun = RuntimeHelpers.GetUninitializedObject(typeof(NRun));
        Require(resolve.Invoke(null, [uninitializedRun]) == null && resolve.Invoke(null, [null]) == null,
            "新对局尚未初始化房间容器时应视为无战斗，不能抛错而中断皮肤提供者切换。");
    }

    private static void CheckHealthAwareIdle()
    {
        var bridge = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.ManagedCharacterAnimationBridge", true)!;
        var resolve = bridge.GetMethod("ResolveAnimation", Static)
            ?? throw new InvalidOperationException("初始待机与攻击后待机缺少统一的血量感知动画选择。");
        var available = new HashSet<string> { "low_health_loop", "idle_loop", "attack1", "death" };
        Func<string, bool> hasAnimation = available.Contains;
        foreach (var (trigger, hp, max, expected) in new (string, int, int, string?)[]
        {
            ("Idle", 70, 70, "idle_loop"),
            ("Idle", 18, 70, "idle_loop"),
            ("Idle", 17, 70, "low_health_loop"),
            ("Idle", 25, 100, "low_health_loop"),
            ("Idle", 0, 0, "idle_loop"),
            ("Revive", 70, 70, "idle_loop"),
            ("Attack", 10, 70, "attack1"),
            ("Dead", 0, 70, "death"),
            ("Unknown", 70, 70, null)
        })
        {
            Require((string?)resolve.Invoke(null, [trigger, hp, max, hasAnimation]) == expected,
                $"{trigger} 在 {hp}/{max} 血量时应选 {expected ?? "不接管"}。");
        }
        available.Remove("low_health_loop");
        Require((string?)resolve.Invoke(null, ["Idle", 1, 70, hasAnimation]) == "idle_loop",
            "皮肤没有残血动画时应回退普通待机。");
        available.Remove("idle_loop");
        available.Add("low_health_loop");
        Require(resolve.Invoke(null, ["Idle", 70, 70, hasAnimation]) == null,
            "缺少普通待机时不能把残血动画当通用回退。");

        var drive = bridge.GetMethod("TryDrive", Static)!;
        Require(PatchProcessor.GetOriginalInstructions(drive).Count(instruction => Equals(instruction.operand, resolve)) >= 2,
            "初始触发与动作后的排队待机必须都走同一个血量判定，不能只修其中一条。");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
