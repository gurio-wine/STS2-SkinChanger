using STS2SkinChanger.Core;

// The reported defect: start-up called NGame.Quit 27 times from sources that were not an exit at
// all (the game ran on to its own normal end). The first call was read as "the player is leaving",
// so the cache session stopped while initialization still had to write the overlay cache - the
// whole start-up failed with "Skin Changer is quitting" and mounted no overlay at all.
internal static class QuitInterceptionTests
{
    public static void Run()
    {
        StartupQuitIsNotAnExit();
        StartupQuitKeepsCacheWritesWorking();
        PostInitializationQuitStopsWritesAndExits();
        StartupCallsAreReportedOnce();
        Console.WriteLine("Quit interception passed: start-up calls are inert, post-initialization calls still exit.");
    }

    // 启动期调 Quit 不置标志、不执行退出清理。
    private static void StartupQuitIsNotAnExit()
    {
        var coordinator = new QuitCleanupCoordinator();
        var cleaned = false;
        var nativeQuit = false;
        var allowed = coordinator.HandleQuit(
            () => { cleaned = true; return Task.FromResult("cleaned"); },
            () => nativeQuit = true,
            _ => { });
        Require(!allowed && !cleaned && !nativeQuit && !coordinator.AllowNativeQuit,
            "初始化未完成时收到 Quit 不能按退出处理：不得置退出标志、不得停缓存写入、不得执行退出清理。");
        Require(!coordinator.InitializationCompleted, "启动期收到 Quit 不能把初始化标记成已完成。");
    }

    // 启动期 Quit 后缓存写入仍成功——这正是报告里失败的写覆盖层缓存。
    private static void StartupQuitKeepsCacheWritesWorking()
    {
        WithStorage((storage, _) =>
        {
            var coordinator = new QuitCleanupCoordinator();
            coordinator.HandleQuit(
                () => { throw new InvalidOperationException("启动期的 Quit 不得启动退出清理。"); },
                () => throw new InvalidOperationException("启动期的 Quit 不得执行原版退出。"),
                _ => { });
            var overlay = Path.Combine(storage.CacheDirectory, "overlay.pck");
            Require(storage.WriteCache(overlay, () => File.WriteAllText(overlay, "overlay")),
                "启动期收到 Quit 之后，初始化必须仍能写覆盖层缓存；被拒就是本次要修的初始化失败。");
            Require(File.Exists(overlay), "启动期收到 Quit 不能阻止覆盖层缓存真正落盘。");
            Require(Directory.Exists(storage.CacheDirectory), "启动期收到 Quit 不能清掉本次会话的缓存目录。");
        });
    }

    // 初始化完成后调 Quit：照旧置标志、停写入，并执行原版退出。
    private static void PostInitializationQuitStopsWritesAndExits()
    {
        WithStorage((storage, _) =>
        {
            var coordinator = new QuitCleanupCoordinator();
            coordinator.MarkInitializationCompleted();
            var cleaned = false;
            var nativeQuit = false;
            var allowed = coordinator.HandleQuit(
                () => Task.Run(() =>
                {
                    cleaned = true;
                    return QuitExitCleanup.Clean(storage);
                }),
                () => nativeQuit = true,
                _ => { });
            Require(!allowed, "初始化完成后的第一次 Quit 由本 Mod 接管，不能让原版再退一次。");
            WaitFor(() => coordinator.AllowNativeQuit, "初始化完成后的 Quit 必须最终执行原版退出。");
            Require(cleaned && nativeQuit, "初始化完成后的 Quit 必须执行退出清理后再执行原版退出。");
            var late = Path.Combine(storage.CacheDirectory, "late.pck");
            Require(!storage.WriteCache(late, () => File.WriteAllText(late, "late")) && !File.Exists(late),
                "初始化完成后的 Quit 必须停止缓存写入，晚到的下载不能重新创建缓存。");
            // 既有功能：退出已在进行/已完成时，后续 Quit 交还原版，且不重入清理。
            var secondCleanup = false;
            Require(coordinator.HandleQuit(
                    () => { secondCleanup = true; return Task.FromResult("again"); },
                    () => { },
                    _ => { }),
                "退出流程已接管后，后续 Quit 必须交还原版退出，不再被拦截。");
            Require(!secondCleanup, "重复的 Quit 不得重入退出清理。");
        });
    }

    // 同一个来源可能连续调用很多次：只报一次，避免把日志刷满。
    private static void StartupCallsAreReportedOnce()
    {
        var coordinator = new QuitCleanupCoordinator();
        var reported = 0;
        for (var call = 0; call < 27; call++)
        {
            coordinator.HandleQuit(
                () => { throw new InvalidOperationException("启动期的 Quit 不得启动退出清理。"); },
                () => throw new InvalidOperationException("启动期的 Quit 不得执行原版退出。"),
                _ => reported++);
        }

        Require(reported == 1, "启动期重复的 Quit 调用只记录一次，其余保持静默。");
        Require(!coordinator.AllowNativeQuit && !coordinator.InitializationCompleted,
            "再多的启动期 Quit 调用都不能把本 Mod 推进退出状态。");
    }

    private static void WithStorage(Action<SkinChangerStorage, string> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "sc-quit-gate-" + Guid.NewGuid().ToString("N"));
        var user = Path.Combine(root, "user");
        var temp = Path.Combine(root, "temp");
        Directory.CreateDirectory(user);
        Directory.CreateDirectory(temp);
        try
        {
            using var storage = new SkinChangerStorage(user, temp, "quit-gate");
            storage.StartCacheSession();
            body(storage, root);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void WaitFor(Func<bool> condition, string reason)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            if (condition()) return;
            Thread.Sleep(10);
        }

        throw new InvalidOperationException(reason);
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }
}
