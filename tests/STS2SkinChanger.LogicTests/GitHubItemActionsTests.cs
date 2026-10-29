using STS2SkinChanger.Core;

internal static class GitHubItemActionsTests
{
    public static void Run()
    {
        void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
        const GitHubInstallState missing = GitHubInstallState.NotInstalled;
        const GitHubInstallState installed = GitHubInstallState.Installed;
        const GitHubInstallState updatable = GitHubInstallState.Updatable;

        Require(GitHubItemActions.Primary(true, missing, false) == GitHubItemAction.Install &&
            GitHubItemActions.Primary(false, missing, false) == GitHubItemAction.Scan,
            "没装的时候只有一个动作：有信息码才能安装，没有信息码只能扫描，而扫描正是作者要发布的那串码的来源。");

        Require(GitHubItemActions.Primary(true, installed, false) == GitHubItemAction.Uninstall,
            "已安装且游戏这一局已经加载过，唯一还有意义的动作是卸载。");
        Require(GitHubItemActions.Primary(true, installed, true) == GitHubItemAction.Restart,
            "刚写进 mods 的包游戏还没加载，要像 Steam 那样给“需要重启”，不能显示成已经生效。");
        Require(GitHubItemActions.Primary(true, updatable, false) == GitHubItemAction.Update &&
            GitHubItemActions.Primary(true, updatable, true) == GitHubItemAction.Restart,
            "有新版本时装新版本；但磁盘上那份连一次都没加载过时，重启优先于更新。");
        Require(GitHubItemActions.Primary(false, installed, false) == GitHubItemAction.Uninstall &&
            GitHubItemActions.Primary(false, installed, true) == GitHubItemAction.Restart,
            "作者删掉 sc.info 之后，已装的目录仍要能卸载或重启，不能被“未识别只给扫描”卡死。");
        Require(GitHubItemActions.Primary(false, missing, true) == GitHubItemAction.Scan,
            "什么都没装时，残留的重启标记不能把按钮变成“需要重启”。");

        Require(GitHubItemActions.ShowsUninstall(GitHubItemAction.Update) && GitHubItemActions.ShowsUninstall(GitHubItemAction.Restart),
            "主按钮让位给更新或重启时，卸载必须留在第二个按钮上，否则装进去的东西再也删不掉。");
        Require(!GitHubItemActions.ShowsUninstall(GitHubItemAction.Install) && !GitHubItemActions.ShowsUninstall(GitHubItemAction.Scan) &&
            !GitHubItemActions.ShowsUninstall(GitHubItemAction.Uninstall),
            "安装、扫描没有东西可删；主按钮本身就是卸载时也不重复显示第二个卸载。");
        Console.WriteLine("GitHub item actions passed: scan, install, restart, update and uninstall routing.");
    }
}
