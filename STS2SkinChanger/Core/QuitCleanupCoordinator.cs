namespace STS2SkinChanger.Core;

// Owns the decision whether a Quit call is the player leaving and, when it is, the ordering and
// re-entry of the exit path. Filesystem and engine boundaries remain independently testable.
internal sealed class QuitCleanupCoordinator
{
    private readonly QuitInterceptionPolicy _interception = new();
    private int _started;

    public bool AllowNativeQuit { get; private set; }

    public bool InitializationCompleted => _interception.InitializationCompleted;

    // Called once the whole start-up chain has finished; until then a Quit call is not an exit.
    public void MarkInitializationCompleted() => _interception.MarkInitializationCompleted();

    // The single decision the game's Quit patch makes. True means the game's own Quit may run now;
    // false means this call must not reach it.
    public bool HandleQuit(Func<Task<string>> cleanup, Action quit, Action<string> log)
    {
        if (!_interception.ShouldInterceptQuit())
        {
            if (_interception.ShouldReportStartupCall())
            {
                log("初始化尚未完成时收到 NGame.Quit：本次调用不按退出处理" +
                    "（不置退出标志、不停缓存写入、不执行退出清理），原版退出流程也不执行。");
            }

            return false;
        }

        if (AllowNativeQuit) return true;
        _ = RunObserved(cleanup, quit, log);
        return false;
    }

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

    // Nobody awaits the exit path, so a fault of the native quit itself would otherwise go unreported.
    private async Task RunObserved(Func<Task<string>> cleanup, Action quit, Action<string> log)
    {
        try { await Run(cleanup, quit, log); }
        catch (Exception e) { log("退出流程未完成，仍按原流程退出：" + e.GetBaseException().Message); }
    }
}
