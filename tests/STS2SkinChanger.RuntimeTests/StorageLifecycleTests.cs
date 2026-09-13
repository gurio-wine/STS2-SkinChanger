using System.Reflection;
using System.Diagnostics;
using STS2SkinChanger;
using STS2SkinChanger.Core;

internal static class StorageLifecycleTests
{
    public static void Run()
    {
        // These tests catch lost presets during migration, over-broad deletion and late
        // network callbacks recreating caches after the player has requested Quit.
        var type = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.SkinChangerStorage");
        Require(type != null, "缺少统一存储与退出缓存清理实现。");
        var root = Path.Combine(Path.GetTempPath(), "sc-storage-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var user = Path.Combine(root, "user");
            var temp = Path.Combine(root, "temp");
            Directory.CreateDirectory(user);
            Directory.CreateDirectory(temp);
            File.WriteAllText(Path.Combine(user, "skin_changer.json"), "{\"Selections\":{\"silent\":\"my-skin\"}}");
            File.WriteAllText(Path.Combine(user, "skin_changer.json.bak"), "backup");
            File.WriteAllText(Path.Combine(user, "skin_changer_theme_presets.json"), "themes");
            File.WriteAllText(Path.Combine(user, "skin_changer_bundle_run_restore.json"), "restore-point");
            Directory.CreateDirectory(Path.Combine(user, "skin_changer_bundle_runs"));
            File.WriteAllText(Path.Combine(user, "skin_changer_bundle_runs", "slot.json"), "run-preset");
            using var storage = (IDisposable)Activator.CreateInstance(type!, user, temp, "test-session")!;
            Call(storage, "MigrateConfiguration");
            var config = (string)Call(storage, "ConfigurationFile", "skin_changer.json")!;
            Require(config == Path.Combine(user, "Gurio.SkinChanger", "skin_changer.json"), "配置必须隔离于游戏根目录。");
            Require(File.ReadAllText(config).Contains("my-skin") && File.ReadAllText(config + ".bak") == "backup", "迁移必须完整保留设置与备份。");
            Require(!File.Exists(Path.Combine(user, "skin_changer.json")), "迁移成功后不能继续在根目录留散文件。");
            Require(File.ReadAllText(Path.Combine(user, "Gurio.SkinChanger", "skin_changer_bundle_runs", "slot.json")) == "run-preset", "不能丢失继续游戏的外观记录。");
            Require(File.ReadAllText(Path.Combine(user, "Gurio.SkinChanger", "skin_changer_bundle_run_restore.json")) == "restore-point", "不能丢失皮肤包恢复点。");
            File.WriteAllText(Path.Combine(user, "skin_changer.json"), "older-conflict");
            Call(storage, "MigrateConfiguration");
            Require(File.ReadAllText(config).Contains("my-skin"), "重复迁移不能覆盖新配置。");
            Require(Directory.EnumerateFiles(Path.Combine(user, "Gurio.SkinChanger", "migration-backup"), "*", SearchOption.AllDirectories).Any(p => File.ReadAllText(p) == "older-conflict"), "有冲突的旧设置必须备份而不是删除。");
            Call(storage, "StartCacheSession");
            var cache = (string)type!.GetProperty("CacheDirectory")!.GetValue(storage)!;
            var image = Path.Combine(cache, "workshop", "covers", "sample.img");
            Require((bool)Call(storage, "WriteCache", image, (Action)(() => File.WriteAllText(image, "image")))!, "活动会话应允许写缓存。");
            File.WriteAllText(Path.Combine(user, "other_mod.json"), "untouched");
            File.WriteAllText(Path.Combine(user, "unrelated.pck"), "original");
            File.WriteAllText(Path.Combine(user, "sts2_skin_overlay_previous.pck"), "legacy-overlay");
            using var other = (IDisposable)Activator.CreateInstance(type, user, temp, "other-session")!;
            Call(other, "StartCacheSession");
            var otherCache = (string)type.GetProperty("CacheDirectory")!.GetValue(other)!;
            var otherImage = Path.Combine(otherCache, "other.img");
            Call(other, "WriteCache", otherImage, (Action)(() => File.WriteAllText(otherImage, "active")));
            Call(storage, "StopCacheWrites");
            Call(storage, "CleanCaches");
            Require(!File.Exists(image) && !Directory.Exists(cache), "退出必须删除当前会话缓存。");
            Require(!File.Exists(Path.Combine(user, "sts2_skin_overlay_previous.pck")), "旧版散落资源缓存也要处理。");
            Require(File.Exists(otherImage), "不能删除另一个仍运行会话的缓存。");
            Require(File.Exists(config) && File.Exists(config + ".bak") && File.Exists(Path.Combine(user, "Gurio.SkinChanger", "skin_changer_theme_presets.json")), "清缓存绝不能删除玩家设置/预设。");
            Require(File.ReadAllText(Path.Combine(user, "other_mod.json")) == "untouched" && File.ReadAllText(Path.Combine(user, "unrelated.pck")) == "original", "不得清理其它 Mod 或不属于 SC 的资源。");
            var late = false;
            Require(!(bool)Call(storage, "WriteCache", image, (Action)(() => late = true))! && !late && !Directory.Exists(cache), "晚到的下载不能重新创建已清理缓存。");
            Call(other, "StopCacheWrites");
            Call(other, "CleanCaches");
            Require(!Directory.Exists(otherCache), "会话结束后其缓存应可清理。");
            if (OperatingSystem.IsWindows())
            {
                var lockedUser = Path.Combine(root, "locked-user");
                Directory.CreateDirectory(lockedUser);
                var oldConfig = Path.Combine(lockedUser, "skin_changer.json");
                File.WriteAllText(oldConfig, "latest");
                File.WriteAllText(oldConfig + ".bak", "older");
                using var lockedStore = new SkinChangerStorage(lockedUser, temp, "locked-config");
                using (var lockedFile = new FileStream(oldConfig, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    lockedStore.MigrateConfiguration();
                    Require(lockedStore.ConfigurationFile("skin_changer.json") == oldConfig && File.Exists(oldConfig + ".bak"), "主配置迁移失败时必须保留整对旧文件，不能误用较旧备份。");
                }
                lockedStore.MigrateConfiguration();
                Require(File.ReadAllText(lockedStore.ConfigurationFile("skin_changer.json")) == "latest", "迁移失败后重试不得丢失最新配置。");
            }
            // A linked cache directory must never cause traversal into another Mod's data.
            var outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "keep.json"), "safe");
            var cacheRoot = Path.Combine(user, "Gurio.SkinChanger", "cache");
            Directory.CreateDirectory(cacheRoot);
            var link = Path.Combine(cacheRoot, "linked-session");
            try { Directory.CreateSymbolicLink(link, outside); }
            catch (UnauthorizedAccessException) { /* Windows may not allow symlinks. */ }
            catch (IOException e) when (OperatingSystem.IsWindows() && (e.HResult & 0xffff) == 1314) { }
            Call(storage, "CleanCaches");
            Require(File.ReadAllText(Path.Combine(outside, "keep.json")) == "safe", "清理不得穿越目录链接。");
            if (Directory.Exists(link)) Directory.Delete(link);
            Console.WriteLine("Storage lifecycle passed: migration, backup conflicts, run records, cleanup boundaries, live sessions and late writes.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static object? Call(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name)!.Invoke(target, args);

    // The quit path deletes the caches it can and leaves the rest behind: the mod no longer ships a
    // helper process, so whatever a mounted resource pack kept open is picked up by the cleanup that
    // runs at the next start. Same ordering, no second executable, and nothing platform-specific left
    // to skip.
    public static void TestQuitAndLockedFiles()
    {
        var gate = new TaskCompletionSource<string>();
        var order = new List<string>();
        var coordinator = new QuitCleanupCoordinator();
        var pending = coordinator.Run(() => { order.Add("clean"); return gate.Task; }, () => order.Add("quit"), _ => { });
        coordinator.Run(() => throw new Exception("duplicate quit"), () => order.Add("duplicate"), _ => { }).GetAwaiter().GetResult();
        Require(!coordinator.AllowNativeQuit && order.SequenceEqual(new[] { "clean" }), "清理结束前不可退出，重复点击不得重入。");
        gate.SetResult("done");
        pending.GetAwaiter().GetResult();
        Require(coordinator.AllowNativeQuit && order.SequenceEqual(new[] { "clean", "quit" }), "清理必须先于原版退出。");
        var failed = new QuitCleanupCoordinator();
        var quitAfterFailure = false;
        failed.Run(() => throw new IOException("locked"), () => quitAfterFailure = true, _ => { }).GetAwaiter().GetResult();
        Require(quitAfterFailure && failed.AllowNativeQuit, "缓存清理失败不能阻止原版保存/退出。");

        var root = Path.Combine(Path.GetTempPath(), "sc-quit-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var user = Path.Combine(root, "user space's 中文");
            var temp = Path.Combine(root, "temp");
            Directory.CreateDirectory(user);
            Directory.CreateDirectory(temp);
            File.WriteAllText(Path.Combine(user, "other_mod.json"), "keep");
            var storage = new SkinChangerStorage(user, temp, "probe");
            try
            {
                storage.StartCacheSession();
                File.WriteAllText(storage.ConfigurationFile("skin_changer.json"), "settings");
                var path = Path.Combine(storage.CacheDirectory, "locked.pck");
                storage.WriteCache(path, () => File.WriteAllText(path, "mounted resource"));
                if (OperatingSystem.IsWindows())
                {
                    // A resource pack this process has mounted: the delete fails while the handle is
                    // open, exactly as it does in the game.
                    using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    storage.StopCacheWrites();
                    var report = storage.CleanCaches();
                    Require(report.Files.Contains(path) && File.Exists(path),
                        "本次游戏仍挂载的缓存文件不能删，必须留在原地并写进报告。");
                }
                else storage.StopCacheWrites();
                using (var next = new SkinChangerStorage(user, temp, "next-session"))
                {
                    next.StartCacheSession();
                    next.CleanCaches();
                    Require(!File.Exists(path) && !Directory.Exists(storage.CacheDirectory),
                        "上次没删成的缓存必须由下一次启动的清理收尾。");
                }
                Require(File.ReadAllText(Path.Combine(user, "other_mod.json")) == "keep" &&
                    File.ReadAllText(storage.ConfigurationFile("skin_changer.json")) == "settings",
                    "清理不能碰其它 Mod 或玩家设置。");
            }
            finally { storage.Dispose(); }
            Console.WriteLine("Quit cleanup passed: native ordering, failure paths, and locked cache files left for the next start.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
