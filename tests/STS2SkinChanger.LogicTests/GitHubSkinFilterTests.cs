using STS2SkinChanger.Core;

internal static class GitHubSkinFilterTests
{
    public static void Run()
    {
        void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
        const GitHubInstallState missing = GitHubInstallState.NotInstalled;
        Require(GitHubSkinFilter.DefaultFilter == GitHubSkinFilter.Missing,
            "GitHub 来源的默认筛选是未安装：打开这个来源就是为了找能装的东西。");
        Require(GitHubSkinFilter.Options.SequenceEqual(["missing", "installed", "updatable", "unrecognized"]),
            "筛选保留未安装/已安装/可更新，并新增未识别；全部由空值表示。");
        Require(!GitHubSkinFilter.IsOption(GitHubSkinFilter.All) && !GitHubSkinFilter.IsOption("bogus"),
            "只有这四个值算筛选值。");

        Require(GitHubSkinFilter.Matches(GitHubSkinFilter.Missing, true, missing) &&
            !GitHubSkinFilter.Matches(GitHubSkinFilter.Missing, false, missing),
            "未安装只收已识别的仓库：没有信息码的仓库没有皮肤可装，只归未识别。");
        Require(!GitHubSkinFilter.Matches(GitHubSkinFilter.Missing, true, GitHubInstallState.Installed),
            "已安装的不能留在未安装里。");
        Require(GitHubSkinFilter.Matches(GitHubSkinFilter.Installed, true, GitHubInstallState.Updatable) &&
            !GitHubSkinFilter.Matches(GitHubSkinFilter.Installed, true, missing) &&
            !GitHubSkinFilter.Matches(GitHubSkinFilter.Installed, false, GitHubInstallState.Installed),
            "已安装同时覆盖已是最新和有待更新两种状态，但未识别的仓库不算已安装。");
        Require(GitHubSkinFilter.Matches(GitHubSkinFilter.Updatable, true, GitHubInstallState.Updatable) &&
            !GitHubSkinFilter.Matches(GitHubSkinFilter.Updatable, true, GitHubInstallState.Installed) &&
            !GitHubSkinFilter.Matches(GitHubSkinFilter.Updatable, false, GitHubInstallState.Updatable),
            "可更新只匹配真的有新版本的那一项，且必须有可读的信息码。");

        Require(GitHubSkinFilter.Matches(GitHubSkinFilter.Unrecognized, false, missing) &&
            !GitHubSkinFilter.Matches(GitHubSkinFilter.Unrecognized, true, missing),
            "未识别只看有没有可读的信息码，与安装状态无关。");
        Require(GitHubSkinFilter.Matches(GitHubSkinFilter.Unrecognized, false, GitHubInstallState.Installed) &&
            !GitHubSkinFilter.Matches(GitHubSkinFilter.Installed, false, GitHubInstallState.Installed) &&
            !GitHubSkinFilter.Matches(GitHubSkinFilter.Missing, false, GitHubInstallState.Installed),
            "作者后来删掉 sc.info 时，已装的目录只属于未识别，未安装与已安装里都不再出现它。");
        Require(GitHubSkinFilter.Matches(GitHubSkinFilter.All, true, GitHubInstallState.Installed) &&
            GitHubSkinFilter.Matches(GitHubSkinFilter.All, false, missing),
            "全部不做任何筛选。");
        Require(GitHubSkinFilter.IgnoresKindFilter(GitHubSkinFilter.Unrecognized) &&
            !GitHubSkinFilter.IgnoresKindFilter(GitHubSkinFilter.Missing) &&
            !GitHubSkinFilter.IgnoresKindFilter(GitHubSkinFilter.All),
            "未识别视图不看类型/目标筛选：未识别的仓库没有类型，留着别的来源选的类型会让这一栏整片空掉。");

        Console.WriteLine("GitHub skin filter passed: install buckets hold recognized repositories only.");
    }
}
