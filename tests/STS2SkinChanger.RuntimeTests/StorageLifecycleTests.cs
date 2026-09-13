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

    public static async Task TestQuitAndLockedFiles()
    {
        var gate = new TaskCompletionSource<string>();
        var order = new List<string>();
        var coordinator = new QuitCleanupCoordinator();
        var pending = coordinator.Run(() => { order.Add("clean"); return gate.Task; }, () => order.Add("quit"), _ => { });
        await coordinator.Run(() => throw new Exception("duplicate quit"), () => order.Add("duplicate"), _ => { });
        Require(!coordinator.AllowNativeQuit && order.SequenceEqual(new[] { "clean" }), "清理结束前不可退出，重复点击不得重入。");
        gate.SetResult("done");
        await pending;
        Require(coordinator.AllowNativeQuit && order.SequenceEqual(new[] { "clean", "quit" }), "清理必须先于原版退出。");
        var failed = new QuitCleanupCoordinator();
        var quitAfterFailure = false;
        await failed.Run(() => throw new IOException("locked"), () => quitAfterFailure = true, _ => { });
        Require(quitAfterFailure && failed.AllowNativeQuit, "缓存清理失败不能阻止原版保存/退出。");
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "sc-quit-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var user = Path.Combine(root, "user space's 中文");
            var temp = Path.Combine(root, "temp");
            Directory.CreateDirectory(user);
            Directory.CreateDirectory(temp);
            File.WriteAllText(Path.Combine(user, "other_mod.json"), "keep");
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var arg in new[] { Assembly.GetExecutingAssembly().Location, "--storage-exit-probe", user, temp }) start.ArgumentList.Add(arg);
            using var child = Process.Start(start)!;
            var output = child.StandardOutput.ReadToEndAsync();
            var errors = child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Require(child.ExitCode == 0, "退出补清理子进程失败：" + await output + await errors);
            var locked = Path.Combine(user, "Gurio.SkinChanger", "cache", "probe", "locked.pck");
            var controls = Path.Combine(user, "Gurio.SkinChanger", "cleanup");
            // The quit helper also finishes the folder work the running game made impossible:
            // swapping in a downloaded skin, and deleting one whose files are still open here.
            var mods = Path.Combine(root, "game", "mods");
            var staging = Path.Combine(user, "Gurio.SkinChanger", "github-pending", "7");
            bool Finished() =>
                !File.Exists(locked) && !Directory.Exists(Path.Combine(mods, "LockedSkin")) &&
                File.Exists(Path.Combine(mods, "StagedSkin", "new.pck")) && !Directory.Exists(staging) &&
                (!Directory.Exists(controls) || !Directory.EnumerateFiles(controls, "targets.json", SearchOption.AllDirectories).Any());
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(20) && !Finished()) await Task.Delay(100);
            Require(!File.Exists(locked), "资源仍被占用时先保留，父进程退出后必须自动删除。" + await output + await errors);
            Require(!Directory.EnumerateDirectories(controls).Any(), "补清理清单和临时控制目录应自行删除。");
            Require(File.ReadAllText(Path.Combine(user, "other_mod.json")) == "keep", "退出助手不能清理其它 Mod。");
            Require(File.ReadAllText(Path.Combine(user, "Gurio.SkinChanger", "skin_changer.json")) == "settings", "退出助手不能删除配置。");
            Require(!Directory.Exists(Path.Combine(mods, "LockedSkin")),
                "游戏进程结束后，退出助手必须删掉本次被占用的皮肤目录。" + await output + await errors);
            Require(File.ReadAllText(Path.Combine(mods, "StagedSkin", "new.pck")) == "new-pack" &&
                !File.Exists(Path.Combine(mods, "StagedSkin", "old.pck")) && !Directory.Exists(staging),
                "退出助手必须用暂存包换掉旧皮肤目录，并在成功后删除暂存包。");
            Require(File.ReadAllText(Path.Combine(mods, "UntouchedSkin", "old.pck")) == "old-pack" &&
                File.Exists(Path.Combine(user, "Gurio.SkinChanger", "evil-staging", "new.pck")),
                "计划里指向暂存目录之外的替换必须被退出助手再次拒绝，不能拿别的目录当包。");
            Require(File.ReadAllText(Path.Combine(root, "evil", "keep.json")) == "safe",
                "计划里越界的目录名不得让退出助手删掉 mods 之外的东西。");
            Console.WriteLine("Quit cleanup passed: native ordering, duplicate/failure paths, real Windows post-process locked-file cleanup and deferred mods folder swaps.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    public static void ExitProbe(string user, string temp)
    {
        using var storage = new SkinChangerStorage(user, temp, "probe");
        storage.StartCacheSession();
        File.WriteAllText(storage.ConfigurationFile("skin_changer.json"), "settings");
        var path = Path.Combine(storage.CacheDirectory, "locked.pck");
        storage.WriteCache(path, () => File.WriteAllText(path, "mounted resource"));
        using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        storage.StopCacheWrites();
        var report = storage.CleanCaches();
        Require(report.Files.Contains(path), "测试必须确实遇到被占用的文件。");
        // An injected out-of-scope target must be rejected again by the external helper.
        report.Files.Add(storage.ConfigurationFile("skin_changer.json"));

        // A skin the game has loaded is held open by this process for the whole session, exactly
        // like the real game holds the assembly and the resource pack it loaded. Both a delete and
        // a swap have to wait for this process to end.
        var game = Path.Combine(Path.GetDirectoryName(user.TrimEnd(Path.DirectorySeparatorChar))!, "game");
        var mods = Path.Combine(game, "mods");
        var lockedSkin = Path.Combine(mods, "LockedSkin");
        var stagedSkin = Path.Combine(mods, "StagedSkin");
        var untouchedSkin = Path.Combine(mods, "UntouchedSkin");
        var staging = Path.Combine(storage.RootDirectory, "github-pending", "7");
        var foreign = Path.Combine(storage.RootDirectory, "evil-staging");
        Directory.CreateDirectory(lockedSkin);
        Directory.CreateDirectory(stagedSkin);
        Directory.CreateDirectory(untouchedSkin);
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(foreign);
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(game)!, "evil"));
        File.WriteAllText(Path.Combine(lockedSkin, "Skin.dll"), "loaded assembly");
        File.WriteAllText(Path.Combine(stagedSkin, "old.pck"), "old-pack");
        File.WriteAllText(Path.Combine(untouchedSkin, "old.pck"), "old-pack");
        File.WriteAllText(Path.Combine(staging, "new.pck"), "new-pack");
        File.WriteAllText(Path.Combine(foreign, "new.pck"), "new-pack");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(game)!, "evil", "keep.json"), "safe");
        using var heldAssembly = new FileStream(Path.Combine(lockedSkin, "Skin.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var heldPack = new FileStream(Path.Combine(stagedSkin, "old.pck"), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var helper = DeferredCacheCleanup.Start(report, storage.RootDirectory, new GitHubModsPlan(mods,
        [
            new(GitHubModsOperationKind.Remove, "LockedSkin", ""),
            new(GitHubModsOperationKind.Replace, "StagedSkin", staging),
            new(GitHubModsOperationKind.Remove, "../evil", ""),
            new(GitHubModsOperationKind.Replace, "UntouchedSkin", foreign)
        ]));
        Require(helper != null, "必须安排一次性退出助手，不依赖下次加载 Mod。");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
