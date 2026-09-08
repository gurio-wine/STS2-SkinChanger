using System.Diagnostics;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;

namespace STS2SkinChanger.Core;

// Intercept the game's final Quit entry, not the confirmation popup. Cancelling the
// popup must do nothing, and the native settings/progress saves still run unchanged.
[HarmonyPatch(typeof(NGame), nameof(NGame.Quit))]
internal static class QuitCacheCleanup
{
    private static readonly QuitCleanupCoordinator Coordinator = new();
    private static bool Prefix(NGame __instance)
    {
        if (Coordinator.AllowNativeQuit) return true;
        TaskHelper.RunSafely(Coordinator.Run(
            () => Task.Run(() =>
            {
                var storage = SkinChangerPaths.Storage;
                storage.StopCacheWrites();
                var report = storage.CleanCaches();
                using var worker = report.Files.Count + report.Directories.Count > 0
                    ? DeferredCacheCleanup.Start(report, storage.RootDirectory) : null;
                var deferred = worker != null;
                return $"退出缓存清理：已删除 {report.RemovedFiles} 个文件；" +
                       $"待处理文件={report.Files.Count}、目录={report.Directories.Count}；" +
                       (deferred ? "已安排在本游戏进程结束后补清理。" :
                        report.Files.Count + report.Directories.Count + report.Errors.Count == 0 ? "清理完成。" : "未完成的缓存清理将在下次启动重试。") +
                       (report.Errors.Count == 0 ? "" : "部分目录无法检查：" + string.Join("；", report.Errors));
            }),
            () => { if (GodotObject.IsInstanceValid(__instance)) __instance.Quit(); },
            ModLog.Info));
        return false;
    }
}

// Owns ordering/re-entry only. Filesystem and engine boundaries remain independently testable.
internal sealed class QuitCleanupCoordinator
{
    private int _started;
    public bool AllowNativeQuit { get; private set; }
    public async Task Run(Func<Task<string>> cleanup, Action quit, Action<string> log)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        try { log(await cleanup()); }
        catch (Exception e) { log("退出缓存清理未全部完成，仍按原流程退出：" + e.GetBaseException().Message); }
        finally
        {
            AllowNativeQuit = true;
            quit();
        }
    }
}

internal static class DeferredCacheCleanup
{
    public static Process? Start(CacheCleanupReport report, string storageRoot)
    {
        if (!OperatingSystem.IsWindows() || report.Files.Count + report.Directories.Count == 0) return null;
        if (!SkinChangerStorage.SafePath(storageRoot, report.UserDirectory)) throw new IOException("Unsafe cleanup control directory.");
        // One directory per owning process prevents two concurrent clients sharing a plan.
        var control = Path.Combine(storageRoot, "cleanup", System.Environment.ProcessId.ToString());
        if (!SkinChangerStorage.SafePath(control, report.UserDirectory)) throw new IOException("Linked cleanup control directory.");
        var worker = Path.Combine(Path.GetDirectoryName(typeof(Entry).Assembly.Location)!, "SkinChanger.CacheCleanup.exe");
        if (!File.Exists(worker)) throw new FileNotFoundException("缺少随 Mod 附带的退出清理程序。", worker);
        Directory.CreateDirectory(control);
        var manifest = Path.Combine(control, "targets.json");
        using var current = Process.GetCurrentProcess();
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            ParentPid = current.Id, ParentStartTicks = current.StartTime.ToUniversalTime().Ticks,
            report.UserDirectory, report.TempDirectory, report.Files, report.Directories
        }));
        var start = new ProcessStartInfo
        {
            FileName = worker,
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add(manifest);
        return Process.Start(start);
    }
}
