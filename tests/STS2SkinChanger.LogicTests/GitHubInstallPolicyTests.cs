using STS2SkinChanger.Core;

internal static class GitHubInstallPolicyTests
{
    public static void Run()
    {
        void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
        void Accept(string name, string expected, long length = 16)
        {
            Require(GitHubZipPolicy.Classify(name, length, 0, 0, out var relative) == GitHubZipEntry.Accept && relative == expected,
                $"正常条目 {name} 必须被接受，并给出用 / 分隔的相对路径 {expected}。");
        }
        Accept("SilentSkinMod-0.1.4/KaguyaSilentRavenSkin.json", "SilentSkinMod-0.1.4/KaguyaSilentRavenSkin.json");
        Accept("KaguyaSilentRavenSkin.pck", "KaguyaSilentRavenSkin.pck");
        Accept(@"sub\win\style.dll", "sub/win/style.dll");
        Require(GitHubZipPolicy.Classify("../evil.dll", 16, 0, 0, out _) == GitHubZipEntry.Reject &&
            GitHubZipPolicy.Classify("a/../../evil.dll", 16, 0, 0, out _) == GitHubZipEntry.Reject,
            "路径穿越条目必须拒绝，不能写到暂存目录之外。");
        Require(GitHubZipPolicy.Classify("/etc/passwd", 16, 0, 0, out _) == GitHubZipEntry.Reject &&
            GitHubZipPolicy.Classify(@"C:\Windows\x.dll", 16, 0, 0, out _) == GitHubZipEntry.Reject,
            "绝对路径与盘符路径必须拒绝。");
        Require(GitHubZipPolicy.Classify("__MACOSX/x", 16, 0, 0, out _) == GitHubZipEntry.Skip &&
            GitHubZipPolicy.Classify(".git/config", 16, 0, 0, out _) == GitHubZipEntry.Skip &&
            GitHubZipPolicy.Classify("mod/", 0, 0, 0, out _) == GitHubZipEntry.Skip,
            "打包者系统的垃圾条目与目录占位要跳过，不算识别失败。");
        Require(GitHubZipPolicy.Classify("big.bin", GitHubZipPolicy.MaxEntryBytes + 1, 0, 0, out _) == GitHubZipEntry.Reject &&
            GitHubZipPolicy.Classify("more.bin", 16, 1, GitHubZipPolicy.MaxTotalBytes, out _) == GitHubZipEntry.Reject &&
            GitHubZipPolicy.Classify("many.bin", 16, GitHubZipPolicy.MaxEntries, 0, out _) == GitHubZipEntry.Reject,
            "单文件、总解压体积和条目数超限都必须拒绝，避免解压炸弹。");

        Require(GitHubInstallPolicy.ModsDirectory(@"C:\Games\SlayTheSpire2\SlayTheSpire2.exe") == @"C:\Games\SlayTheSpire2\mods",
            "Windows 安装目标是与可执行文件同级的 mods 目录。");
        Require(GitHubInstallPolicy.ModsDirectory("/Applications/Game.app/Contents/MacOS/Game") ==
            Path.GetFullPath("/Applications/Game.app/Contents/Resources/mods"),
            "macOS 应用包的 mods 目录在 Contents/Resources 下，与主资源包同层。");
        Require(GitHubInstallPolicy.FolderName("KaguyaSilentRavenSkin", "SilentSkinMod") == "KaguyaSilentRavenSkin",
            "安装目录优先用清单 ID，与游戏加载本地 Mod 的布局一致。");
        Require(GitHubInstallPolicy.FolderName("../evil", "SilentSkinMod") == "SilentSkinMod" &&
            GitHubInstallPolicy.FolderName("", "SilentSkinMod") == "SilentSkinMod" &&
            GitHubInstallPolicy.FolderName("../evil", "a/b\\c") == "abc",
            "非法清单 ID 必须回退到仓库名，且仓库名里的路径分隔符不能进入目录名。");

        Require(GitHubInstallPolicy.CompareVersions("0.1.10", "0.1.4") > 0 && GitHubInstallPolicy.CompareVersions("v0.1.4", "0.1.4") == 0 &&
            GitHubInstallPolicy.CompareVersions("1.0", "1.0.0") == 0 && GitHubInstallPolicy.CompareVersions("", "1.0") < 0,
            "版本比较按数字段，忽略前导 v，缺段按 0。");
        Require(GitHubInstallPolicy.Classify(null, "0.1.4") == GitHubInstallState.NotInstalled &&
            GitHubInstallPolicy.Classify("0.1.4", "0.1.4") == GitHubInstallState.Installed &&
            GitHubInstallPolicy.Classify("0.1.4", "0.1.10") == GitHubInstallState.Updatable &&
            GitHubInstallPolicy.Classify("0.1.4", null) == GitHubInstallState.Installed,
            "安装状态只按已安装版本与可用版本判定；拿不到可用版本时不能谎报可更新。");

        var root = Path.Combine(Path.GetTempPath(), "sc-gh-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "KaguyaSilentRavenSkin.json"),
                """{"id":"KaguyaSilentRavenSkin","name":"Silent","version":"0.1.4","has_pck":true,"affects_gameplay":false}""");
            Require(GitHubInstallPolicy.ReadManifestVersion(root, "KaguyaSilentRavenSkin") == "0.1.4",
                "已安装版本必须从清单 json 的 version 字段读取。");
            File.WriteAllText(Path.Combine(root, "broken.json"), "{");
            Require(GitHubInstallPolicy.ReadManifestVersion(root, "broken") == null &&
                GitHubInstallPolicy.ReadManifestVersion(root, "../escape") == null,
                "损坏清单与非法 ID 都要返回 null，不能抛异常或读到目录外。");

            // 卸载只认自己写下的清单：同名的手工目录必须留下。
            var mods = Path.Combine(root, "mods");
            var installed = Path.Combine(mods, "KaguyaSilentRavenSkin");
            Directory.CreateDirectory(installed);
            File.Copy(Path.Combine(root, "KaguyaSilentRavenSkin.json"), Path.Combine(installed, "KaguyaSilentRavenSkin.json"));
            Directory.CreateDirectory(Path.Combine(mods, "PlayerMade"));
            Require(GitHubInstallPolicy.UninstallTarget(mods, "KaguyaSilentRavenSkin", "KaguyaSilentRavenSkin") == installed,
                "卸载要能删掉我们自己安装的目录。");
            Require(GitHubInstallPolicy.UninstallTarget(mods, "PlayerMade", "PlayerMade") == null &&
                GitHubInstallPolicy.UninstallTarget(mods, "Missing", "Missing") == null,
                "没有我们自己写下的清单就不能删：玩家手工放的目录和不存在的目录都必须返回 null。");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("GitHub install policy passed: zip guards, install path, version state, manifest version.");
    }
}
