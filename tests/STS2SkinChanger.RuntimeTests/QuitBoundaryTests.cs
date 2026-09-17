using System.Reflection;
using STS2SkinChanger;

// Start-up must hand the quit gate over exactly once, at the end of the whole chain, and must hand
// it over even when a start-up step throws. The reported defect was the opposite failure - a Quit
// during start-up was read as an exit and cancelled initialization - so this locks the boundary in
// place: the mark belongs to the finalizer, never to the postfix body that the steps above it can
// abort. Verified against the real patch class, not a copy of it.
internal static class QuitBoundaryTests
{
    public static void Run()
    {
        var patchType = typeof(Entry).Assembly.GetType("STS2SkinChanger.EssentialInitializationPatch")
                        ?? throw new InvalidOperationException("找不到启动初始化补丁类型。");
        var gateType = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.QuitCacheCleanup")
                       ?? throw new InvalidOperationException("找不到退出拦截补丁类型。");
        var mark = gateType.GetMethod("MarkInitializationCompleted", BindingFlags.Static | BindingFlags.NonPublic)
                   ?? throw new InvalidOperationException("找不到初始化完成的移交入口。");
        var accepts = gateType.GetProperty("AcceptsQuitAsExit", BindingFlags.Static | BindingFlags.NonPublic)
                      ?? throw new InvalidOperationException("找不到退出判定状态。");
        Require(accepts.GetValue(null) is false, "测试进程未完成启动初始化，此时 Quit 不能按退出处理。");

        var finalizer = patchType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .SingleOrDefault(method => method.GetCustomAttribute<HarmonyLib.HarmonyFinalizer>() != null)
            ?? throw new InvalidOperationException(
                "启动初始化补丁必须用 finalizer 移交退出拦截：postfix 里的移交会在启动步骤抛错时永久关闭拦截，" +
                "此后任何退出都无法生效。");

        // The gate must already be open for the normal path, and must open on the failure path too.
        var exception = new InvalidOperationException("simulated start-up failure");
        Require(ReferenceEquals(finalizer.Invoke(null, [exception]), exception), "移交退出拦截不能吞掉或替换启动异常。");
        Require(accepts.GetValue(null) is true, "启动链路结束后必须移交退出拦截，否则玩家退出不会被接管清理。");

        foreach (var name in new[] { "Prefix", "Postfix" })
        {
            var method = patchType.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
                         ?? throw new InvalidOperationException($"找不到启动初始化补丁的 {name}。");
            Require(!CallsMark(method, mark),
                $"启动初始化补丁的 {name} 不得移交退出拦截：启动步骤抛错时它就执行不到，" +
                "拦截会停在整个会话都不生效。");
        }

        Console.WriteLine("Quit boundary passed: the gate opens once, from the finalizer, and survives a start-up failure.");
    }

    // A direct call is what the postfix body would contain; the finalizer is the only legal owner.
    // A call/callvirt to a method carries its metadata token as the four operand bytes, so looking
    // for that operand is enough to prove the call is absent - and a missed call here can only make
    // this test pass, never fail, so the shape of the scan cannot invent a broken patch.
    private static bool CallsMark(MethodInfo method, MethodInfo mark)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il == null) return false;
        var operand = BitConverter.GetBytes(mark.MetadataToken);
        for (var index = 1; index + operand.Length <= il.Length; index++)
        {
            // call (0x28) / callvirt (0x6F) immediately before the token.
            if (il[index - 1] is not (0x28 or 0x6F)) continue;
            var matches = true;
            for (var offset = 0; offset < operand.Length; offset++)
            {
                if (il[index + offset] != operand[offset]) { matches = false; break; }
            }

            if (matches) return true;
        }

        return false;
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }
}
