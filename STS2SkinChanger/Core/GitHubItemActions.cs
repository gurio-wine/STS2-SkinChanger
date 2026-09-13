namespace STS2SkinChanger.Core;

// What the single action button on a GitHub card does. Everything it is decided from is
// local: whether the repository published a code at all, what state the folder this mod
// wrote is in, and whether the running game has loaded that folder yet.
internal enum GitHubItemAction { Scan, Install, Restart, Update, Uninstall }

internal static class GitHubItemActions
{
    public static GitHubItemAction Primary(bool recognized, GitHubInstallState state, bool pendingRestart)
    {
        // Nothing installed: a repository with a code can be installed, one without a code can
        // only be scanned, because that scan is what produces the code its author publishes.
        if (state == GitHubInstallState.NotInstalled) return recognized ? GitHubItemAction.Install : GitHubItemAction.Scan;
        // The package is on disk but the running game has never scanned it, so nothing of it is
        // loaded: the restart is the one action that changes that, exactly like a Steam item
        // waiting for its restart. It wins over a waiting update, because the copy on disk has
        // not even been loaded once.
        if (pendingRestart) return GitHubItemAction.Restart;
        return state == GitHubInstallState.Updatable ? GitHubItemAction.Update : GitHubItemAction.Uninstall;
    }

    // Removing the installed copy stays reachable whenever the primary button carries something
    // else: a waiting update, or a waiting restart.
    public static bool ShowsUninstall(GitHubItemAction action) => action is GitHubItemAction.Update or GitHubItemAction.Restart;
}
