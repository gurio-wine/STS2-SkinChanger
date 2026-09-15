using STS2SkinChanger.Core;

internal static class GitHubModsPolicyTests
{
    private const string DigestA = "206ffa80a0b9f747c8a8fc3b909563158f6f9645fa7b39f029cc30b76e6c8c54";

    public static void Run()
    {
        void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }

        // Every value here ends up as a real path inside the game's mods folder, and a deletion
        // target at that: path separators, parent directories, empty and over-long names are out.
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

        // The loader reads a directory as a mod only when it finds a file whose name ends in
        // ".json" (ModManager.ReadModsInDirRecursive), so hiding a skin is a rename - no file the
        // running game holds open is touched by it.
        Require(GitHubModsPolicy.IsManifestName("Skin.json") && GitHubModsPolicy.IsManifestName("cards.json"),
            "任何以 .json 结尾的文件都会被游戏当作清单读取。");
        Require(!GitHubModsPolicy.IsManifestName("Skin.json.disabled") &&
            !GitHubModsPolicy.IsManifestName("Skin.json.bak") && !GitHubModsPolicy.IsManifestName("Skin.JSON") &&
            !GitHubModsPolicy.IsManifestName("Skin.jsonx") && !GitHubModsPolicy.IsManifestName("json") &&
            !GitHubModsPolicy.IsManifestName(null),
            "改名后的清单、备份、其它扩展名与空名都不再是清单。");
        Require(GitHubModsPolicy.DisabledManifestName("Skin") == "Skin.json.disabled" &&
            !GitHubModsPolicy.IsManifestName(GitHubModsPolicy.DisabledManifestName("Skin")),
            "停用后的清单名必须让游戏认不出来，否则旧目录下次启动会和新目录一起被加载。");

        // The folder a new package goes into while the game holds the old one open: the canonical
        // name plus a token from the package digest.
        Require(GitHubModsPolicy.DigestToken(DigestA) == "206ffa80" &&
            GitHubModsPolicy.DigestToken("sha256:" + DigestA) == "206ffa80" &&
            GitHubModsPolicy.DigestToken(DigestA.ToUpperInvariant()) == "206ffa80",
            "标记取包校验值的前 8 位，GitHub 的 sha256: 前缀与大写都要能读。");
        Require(GitHubModsPolicy.DigestToken("") == "" && GitHubModsPolicy.DigestToken("206ffa8") == "" &&
            GitHubModsPolicy.DigestToken("md5:junk") == "" && GitHubModsPolicy.DigestToken(null) == "",
            "读不出校验值时不给标记，不能拿半个值当目录名。");
        Require(GitHubModsPolicy.FreshFolderName("KaguyaSilentRavenSkin", "206ffa80") == "KaguyaSilentRavenSkin-206ffa80",
            "备用目录名＝原目录名＋标记。");
        var longest = new string('9', 64);
        var shortened = GitHubModsPolicy.FreshFolderName(longest, "206ffa80");
        Require(shortened.Length <= 64 && shortened.EndsWith("-206ffa80", StringComparison.Ordinal) &&
            GitHubInstallPolicy.IsSafeToken(shortened),
            "超长目录名必须截短到放得下标记，否则这个皮肤永远更新不了。");
        Require(GitHubModsPolicy.FreshFolderName("Skin", "206ffa80") == "Skin-206ffa80" &&
            GitHubInstallPolicy.IsSafeToken(GitHubModsPolicy.FreshFolderName("Skin", "206ffa80")),
            "备用目录名必须是台账能存下的安全名。");
        Require(GitHubModsPolicy.FreshFolderName("Skin", "../evil") == "" &&
            GitHubModsPolicy.FreshFolderName("Skin", ".hidden") == "" &&
            GitHubModsPolicy.FreshFolderName("Skin", "a/b") == "" &&
            GitHubModsPolicy.FreshFolderName("Skin", "") == "" &&
            GitHubModsPolicy.FreshFolderName("../evil", "206ffa80") == "" &&
            GitHubModsPolicy.FreshFolderName("Skin.", "206ffa80") == "",
            "目录名或标记不合法时不能凑出一个要写到磁盘上的名字。");

        // The one destructive step left: only a folder this mod wrote, and only while nothing on
        // disk claims it. A folder an install points at now is in use, and a folder holding a live
        // manifest has been written again since (a re-install, or the player's own copy).
        Require(GitHubModsPolicy.CanDeleteFolder("Skin", live: false, holdsManifest: false),
            "本 Mod 写过、已经没有清单、也不是在用目录的旧目录必须允许删除。");
        Require(!GitHubModsPolicy.CanDeleteFolder("Skin", live: true, holdsManifest: false),
            "台账正指着的目录是在用目录，绝不能删。");
        Require(!GitHubModsPolicy.CanDeleteFolder("Skin", live: false, holdsManifest: true),
            "重新出现清单的目录不是本 Mod 停用的那一个了，绝不能删。");
        Require(!GitHubModsPolicy.CanDeleteFolder("../evil", live: false, holdsManifest: false) &&
            !GitHubModsPolicy.CanDeleteFolder(".hidden", live: false, holdsManifest: false) &&
            !GitHubModsPolicy.CanDeleteFolder("Skin", live: true, holdsManifest: true) &&
            !GitHubModsPolicy.CanDeleteFolder("", live: false, holdsManifest: false) &&
            !GitHubModsPolicy.CanDeleteFolder(null, live: false, holdsManifest: false),
            "非法目录名一律不许删。");

        Console.WriteLine("GitHub mods policy passed: folder tokens, manifest rule, fresh names, delete guard.");
    }
}
