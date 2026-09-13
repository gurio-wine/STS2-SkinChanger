using STS2SkinChanger.Core;

internal static class GitHubModsPolicyTests
{
    public static void Run()
    {
        void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }

        // A step only ever names one folder inside the game's mods folder: every value here ends up
        // as a real path the quit helper walks, and that helper runs while no game is watching.
        Require(GitHubModsPolicy.IsModsFolderName("KaguyaSilentRavenSkin") &&
            GitHubModsPolicy.IsModsFolderName("a.b-c_1"),
            "合法的目录名必须被接受。");
        Require(!GitHubModsPolicy.IsModsFolderName("../evil") && !GitHubModsPolicy.IsModsFolderName("a/b") &&
            !GitHubModsPolicy.IsModsFolderName("a\\b") && !GitHubModsPolicy.IsModsFolderName(".") &&
            !GitHubModsPolicy.IsModsFolderName("..") && !GitHubModsPolicy.IsModsFolderName("") &&
            !GitHubModsPolicy.IsModsFolderName(new string('9', 65)) && !GitHubModsPolicy.IsModsFolderName("皮肤"),
            "路径分隔符、上级目录、空名与超长名都必须被拒绝。");
        Require(GitHubModsPolicy.IsLeftoverFolderName(".KaguyaSilentRavenSkin.installing") &&
            GitHubModsPolicy.IsLeftoverFolderName(".Skin.previous"),
            "本 Mod 中断安装留下的临时目录必须能被认出来，才能安排删除。");
        Require(!GitHubModsPolicy.IsLeftoverFolderName("KaguyaSilentRavenSkin") &&
            !GitHubModsPolicy.IsLeftoverFolderName(".player-folder") &&
            !GitHubModsPolicy.IsLeftoverFolderName(".x.installing.bak") &&
            !GitHubModsPolicy.IsLeftoverFolderName(".a.installing\\.."),
            "玩家自己的目录和任何带路径的名都不能被当成临时目录删掉。");

        var stagedA = Path.Combine(Path.GetTempPath(), "sc-gh-pending", "1");
        var plan = GitHubModsPolicy.QuitPlan(
            [
                new(GitHubModsOperationKind.Remove, "Skin", ""),
                new(GitHubModsOperationKind.Replace, "Skin", stagedA),
                new(GitHubModsOperationKind.Replace, "../evil", stagedA),
                new(GitHubModsOperationKind.Remove, "Other", "")
            ],
            [".Skin.installing", ".Skin.installing", "../evil", "PlayerFolder"]);
        Require(plan.Length == 3 &&
            plan[0] is { Kind: GitHubModsOperationKind.Replace, Folder: "Skin", Staging: var first } && first == stagedA &&
            plan[1] is { Kind: GitHubModsOperationKind.Remove, Folder: "Other" } &&
            plan[2] is { Kind: GitHubModsOperationKind.Remove, Folder: ".Skin.installing" },
            "同一目录既排了卸载又排了替换时只能替换，非法名与玩家自己的目录不得进入计划，临时目录只排一次。");

        Require(!GitHubModsPolicy.KeepQueued(GitHubModsOperationKind.Remove, folderExists: false, stagingExists: false) &&
            GitHubModsPolicy.KeepQueued(GitHubModsOperationKind.Remove, folderExists: true, stagingExists: false) &&
            GitHubModsPolicy.KeepQueued(GitHubModsOperationKind.Replace, folderExists: true, stagingExists: true) &&
            !GitHubModsPolicy.KeepQueued(GitHubModsOperationKind.Replace, folderExists: true, stagingExists: false) &&
            !GitHubModsPolicy.KeepQueued(GitHubModsOperationKind.Replace, folderExists: false, stagingExists: true),
            "退出助手执行过的步骤、玩家手动删掉的目录、暂存包已丢失的替换，都要在下次启动时作废，不能一直重试。");

        var staging = GitHubModsPolicy.StagingDirectory(@"C:\Users\player\AppData\Roaming\SlayTheSpire2\Gurio.SkinChanger", 12345);
        Require(staging == @"C:\Users\player\AppData\Roaming\SlayTheSpire2\Gurio.SkinChanger\github-pending\12345" &&
            !GitHubModsPolicy.StagingDirectory(@"C:\sc", 1).Contains("mods", StringComparison.OrdinalIgnoreCase),
            "暂存目录必须在 Mod 自己的配置目录里：放进 mods 会被游戏当成第二个同 ID 皮肤。");

        Console.WriteLine("GitHub mods policy passed: folder tokens, leftover names, quit plan, reconciliation, staging root.");
    }
}
