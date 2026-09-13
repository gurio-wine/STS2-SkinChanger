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
                // Cache files the running game still has mounted cannot be deleted here; they are
                // reported and picked up by the cleanup the next start runs. Nothing outlives this
                // process any more: an earlier version started a helper executable for that job, which
                // no platform without it could run and which antivirus software read as a dropper.
                var report = storage.CleanCaches();
                var pending = report.Files.Count + report.Directories.Count;
                return $"退出缓存清理：已删除 {report.RemovedFiles} 个文件；" +
                       (pending + report.Errors.Count == 0 ? "清理完成。" : $"未完成的 {pending} 项缓存清理将在下次启动重试。") +
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
