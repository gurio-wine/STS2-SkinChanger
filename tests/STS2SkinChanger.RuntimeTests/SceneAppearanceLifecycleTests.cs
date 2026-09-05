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
        CheckMerchantRoomScopeOwnership();
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
            merchantPrefix.Invoke(null, new object?[merchantPrefix.GetParameters().Length]);
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
            merchantPrefix.Invoke(null, new object?[merchantPrefix.GetParameters().Length]);
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

    private static void CheckMerchantRoomScopeOwnership()
    {
        var assembly = typeof(Entry).Assembly;
        var ownership = assembly.GetType("STS2SkinChanger.Ui.RoomRuntimeScopeOwnership")
            ?? throw new InvalidOperationException("商人退出缺少房间所有权检查，旧商店会停用新商店的皮肤行为。");
        var record = ownership.GetMethod("Record", Static)!;
        var refresh = ownership.GetMethod("Refresh", Static)!;
        var release = ownership.GetMethod("Release", Static)!;
        var service = assembly.GetType("STS2SkinChanger.Core.SkinService", true)!;
        var scope = service.GetField("_runtimeProviderBehaviorScope", Static)!;
        var focus = service.GetMethod("FocusRuntimeProviderBehaviorsOnGroups", Static)!;
        var savedScope = scope.GetValue(null);
        long Claim(string group) => (long)focus.Invoke(null,
            [new[] { group }, Array.Empty<string>(), "test room ownership"])!;
        try
        {
            foreach (var (outgoing, incoming) in new[]
            {
                ("merchant", "merchant"),
                ("merchant", "monster:test"),
                ("fake_merchant_monster", "merchant"),
                ("fake_merchant_monster", "fake_merchant_monster")
            })
            {
                var oldRoom = new object();
                var newRoom = new object();
                record.Invoke(null, [oldRoom, Claim(outgoing)]);
                record.Invoke(null, [newRoom, Claim(incoming)]);
                var incomingScope = scope.GetValue(null);
                release.Invoke(null, [oldRoom]);
                Require(ReferenceEquals(incomingScope, scope.GetValue(null)),
                    $"{outgoing} 退出不能停用提前创建的 {incoming}，即使两房间使用相同提供者。");
                release.Invoke(null, [newRoom]);
                Require(!ReferenceEquals(incomingScope, scope.GetValue(null)),
                    "当前商人房间正常退出仍须释放范围，不能禁用所有清理来绕过问题。");
                var clearedScope = scope.GetValue(null);
                release.Invoke(null, [newRoom]);
                Require(ReferenceEquals(clearedScope, scope.GetValue(null)), "重复退出不得再次清理范围。");
            }

            var activeRoom = new object();
            record.Invoke(null, [activeRoom, Claim("merchant")]);
            refresh.Invoke(null, [activeRoom, Claim("merchant")]);
            var refreshedScope = scope.GetValue(null);
            release.Invoke(null, [activeRoom]);
            Require(!ReferenceEquals(refreshedScope, scope.GetValue(null)),
                "当前商店切肤刷新之后，正常退出仍必须释放最新范围。");

            var unownedPreview = new object();
            refresh.Invoke(null, [unownedPreview, Claim("merchant")]);
            record.Invoke(null, [unownedPreview, 0L]);
            var liveScope = scope.GetValue(null);
            release.Invoke(null, [unownedPreview]);
            Require(ReferenceEquals(liveScope, scope.GetValue(null)),
                "图鉴、未登记房间或失败的范围创建不能获取局内房间的清理权限。");
        }
        finally
        {
            scope.SetValue(null, savedScope);
        }

        // The state-machine tests above need no native Godot process. Check that the real
        // Harmony boundaries actually use it, including both possible merchant Create results.
        foreach (var patchName in new[] { "MerchantRoomPreviewExitTreePatch", "FakeMerchantPreviewExitTreePatch" })
        {
            var exit = assembly.GetType("STS2SkinChanger.Ui." + patchName, true)!.GetMethod("Postfix", Static)!;
            Require(Calls(exit, release), patchName + " 必须按房间所有权退出。");
        }
        var create = assembly.GetType("STS2SkinChanger.Ui.MerchantRoomCreateAppearancePatch", true)!;
        Require(create.GetMethod("Prefix", Static)!.GetParameters().Any(p => p.Name == "__state" && p.IsOut),
            "商店创建前取得的范围必须通过 Harmony __state 交给实际创建的房间。");
        Require(PatchProcessor.GetOriginalInstructions(create.GetMethod("Postfix", Static)!)
                .Count(instruction => Equals(instruction.operand, record)) == 2,
            "预加载回退房间与当前选择的替代房间都必须登记清理所有权。");
        var fakeReady = assembly.GetType("STS2SkinChanger.Ui.FakeMerchantAppearancePatch", true)!.GetMethod("Prefix", Static)!;
        Require(Calls(fakeReady, record), "假商人也必须登记自身的范围所有权。");
        var runtime = assembly.GetType("STS2SkinChanger.Ui.CharacterAppearanceRuntime", true)!;
        Require(runtime.GetMethods(Static).Any(method => Calls(method, ownership.GetMethod("RefreshTree", Static)!)),
            "局内显式刷新必须续期当前房间，不能让正常退出永久失去清理权限。");
    }

    private static bool Calls(MethodInfo method, MethodInfo target) =>
        method.GetMethodBody() != null && PatchProcessor.GetOriginalInstructions(method)
            .Any(instruction => Equals(instruction.operand, target));

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
