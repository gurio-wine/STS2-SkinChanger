namespace STS2SkinChanger.Core;

// NGame.Quit is not only the player leaving. One reported start-up session called it 27 times from
// sources that were not an exit at all: the game ran on to its own normal end. Our patch read the
// first of those calls as "the player is leaving" - it stopped the cache session while the start-up
// initialization still had to write the overlay cache, so the whole initialization failed with
// "Skin Changer is quitting" (WriteCache -> MountArchiveOverlay -> MountOverlay ->
// InitializeBeforeAssets) and that session mounted no overlay at all.
//
// So a Quit that arrives before this mod finished initializing is not an exit: it raises no exit
// flag, stops no cache writes, and starts no cleanup. It is not passed to the game's own Quit
// either - that body ends in GetTree().Quit(), and the reported session proves such calls arrive
// while the game is meant to keep running (only the calls our patch already suppressed let it keep
// running). Nothing is lost by that: folder work the exit path would have started is designed to be
// picked up by the next start, and the caches a killed session leaves behind are cleaned there too.
internal sealed class QuitInterceptionPolicy
{
    private int _initializationCompleted;
    private int _startupCallReported;

    public bool InitializationCompleted => Volatile.Read(ref _initializationCompleted) == 1;

    // Called once the whole start-up chain has finished; until then a Quit call is not an exit.
    public void MarkInitializationCompleted() => Interlocked.Exchange(ref _initializationCompleted, 1);

    public bool ShouldInterceptQuit() => InitializationCompleted;

    // Reports only the first start-up call, so a caller that keeps calling cannot flood the log.
    public bool ShouldReportStartupCall() =>
        !InitializationCompleted && Interlocked.Exchange(ref _startupCallReported, 1) == 0;
}
