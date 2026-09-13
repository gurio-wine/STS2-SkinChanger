using System.Security.Cryptography;
using System.Text;
using STS2SkinChanger.Core;

internal static class GitHubInstallPolicyTests
{
    private const string DigestA = "206ffa80a0b9f747c8a8fc3b909563158f6f9645fa7b39f029cc30b76e6c8c54";
    private const string DigestB = "54492e1cb08df47c5b7b8bcbd27a04e4ab471c078c96d8a897cd093b0ead33ad";

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
        Require(GitHubInstallPolicy.Classify(null, null, "0.1.4", null) == GitHubInstallState.NotInstalled &&
            GitHubInstallPolicy.Classify("0.1.4", "", "0.1.4", "") == GitHubInstallState.Installed &&
            GitHubInstallPolicy.Classify("0.1.4", "", "0.1.10", "") == GitHubInstallState.Updatable &&
            GitHubInstallPolicy.Classify("0.1.4", "", null, null) == GitHubInstallState.Installed,
            "两边都没有校验值时按已安装版本与可用版本判定；拿不到可用版本时不能谎报可更新。");
        // tag 由作者随便起（latest、2026-09-10、v2-final），只有包内容的校验值才说明"要不要更新"。
        Require(GitHubInstallPolicy.Classify("latest", DigestA, "latest", DigestA) == GitHubInstallState.Installed &&
            GitHubInstallPolicy.Classify("latest", DigestA, "latest", DigestB) == GitHubInstallState.Updatable &&
            GitHubInstallPolicy.Classify("latest", DigestA, "latest", DigestA.ToUpperInvariant()) == GitHubInstallState.Installed,
            "tag 不是版本号时只能按校验值判断：内容没变就是最新，变了才算更新，大小写不同不算变化。");
        Require(GitHubInstallPolicy.Classify("v2-final", DigestA, "nightly", DigestB) == GitHubInstallState.Updatable,
            "换了 tag 且包也换了时必须提示更新，哪怕两个 tag 都读不出数字。");
        Require(GitHubInstallPolicy.Classify("0.1.4", DigestA, "0.1.5", DigestA) == GitHubInstallState.Installed,
            "换了 tag 但包内容一模一样时不算更新：要装的还是同一份文件。");
        Require(GitHubInstallPolicy.Classify("0.1.4", DigestA, "0.1.4", "") == GitHubInstallState.Installed &&
            GitHubInstallPolicy.Classify("0.1.4", DigestA, "0.1.5", "") == GitHubInstallState.Updatable &&
            GitHubInstallPolicy.Classify("0.1.4", "", "0.1.5", DigestB) == GitHubInstallState.Updatable,
            "远端没有校验值（老资产缺这个字段）时退回按版本号比较，既不谎报也不漏报更新。");
        Require(GitHubInstallPolicy.Classify("", DigestA, "", DigestB) == GitHubInstallState.Updatable &&
            GitHubInstallPolicy.Classify("", DigestA, "", DigestA) == GitHubInstallState.Installed &&
            GitHubInstallPolicy.Classify("", DigestA, "", "") == GitHubInstallState.Installed,
            "没有版本号的包同样要能判断更新，而且拿不到远端校验值时不能凭空说可更新。");
        Require(GitHubInstallPolicy.Classify(null, null, "0.1.4", DigestB) == GitHubInstallState.NotInstalled &&
            GitHubInstallPolicy.Classify("", DigestA, "0.1.4", DigestB) != GitHubInstallState.NotInstalled,
            "只有版本号和校验值都空才是未安装；有校验值但没有版本号的安装记录仍然是已安装。");
        // 加校验值之前写下的安装记录只有版本号，只能按版本号比较。刷新时看到发布方公布的校验值
        // 就补记一次，此后这条记录也按包内容判断——tag 一个字都不改、只把包换掉也能发现更新。
        Require(GitHubInstallPolicy.BackfillDigest("0.1.4", "", "v0.1.4-beta", DigestA) == DigestA &&
            GitHubInstallPolicy.BackfillDigest("0.1.4", null, "0.1.4", "sha256:" + DigestA) == DigestA &&
            // 真实台账里的那一条：安装时记下的是 tag（v0.1.4），发布方的 tag 是 v0.1.4-beta。
            GitHubInstallPolicy.BackfillDigest("v0.1.4", null, "v0.1.4-beta", "sha256:" + DigestA) == DigestA,
            "没有校验值的安装记录要在刷新时补记发布方公布的校验值，带 sha256: 前缀的也要认。");
        var adopted = GitHubInstallPolicy.BackfillDigest("0.1.4", "", "v0.1.4-beta", DigestA);
        Require(GitHubInstallPolicy.Classify("0.1.4", adopted, "v0.1.4-beta", DigestA) == GitHubInstallState.Installed &&
            GitHubInstallPolicy.Classify("0.1.4", adopted, "v0.1.4-beta", DigestB) == GitHubInstallState.Updatable,
            "补记之后换 tag 不再是唯一的更新信号：tag 完全相同、包被换掉也必须显示为可更新。");
        Require(GitHubInstallPolicy.BackfillDigest("0.1.4", "", "0.1.5", DigestB).Length == 0 &&
            GitHubInstallPolicy.BackfillDigest("0.1.4", "", "latest", "").Length == 0 &&
            GitHubInstallPolicy.BackfillDigest("", "", "0.1.4", DigestA).Length == 0 &&
            GitHubInstallPolicy.BackfillDigest("0.1.4", DigestB, "0.1.5", DigestA).Length == 0 &&
            GitHubInstallPolicy.BackfillDigest("0.1.4", "", "0.1.5", "not-a-digest").Length == 0,
            "版本号仍说这个安装比发布的新时不能补记（补了就把真实更新抹掉）；发布方没有校验值、还没有安装记录或已经记过时都不补。");
        // 缓存里的包只有在"就是现在发布的那一份"时才能复用：大小一样也可能是被换过的文件。
        Require(GitHubInstallPolicy.CanReusePackage(DigestA, 100, 100, DigestA) &&
            !GitHubInstallPolicy.CanReusePackage(DigestA, 100, 100, DigestB) &&
            !GitHubInstallPolicy.CanReusePackage(DigestA, 100, 99, DigestA) &&
            !GitHubInstallPolicy.CanReusePackage(DigestA, 100, -1, ""),
            "发布方给了校验值时，大小相同但内容不同的旧包必须重新下载，否则装上去的还是旧文件，而卡片会说它已是最新。");
        Require(GitHubInstallPolicy.CanReusePackage("", 100, 100, DigestB) &&
            !GitHubInstallPolicy.CanReusePackage("", 100, -1, ""),
            "发布方没给校验值时只能按大小判断，与加校验值之前的缓存行为一致。");

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

            var probe = Path.Combine(root, "package.zip");
            File.WriteAllBytes(probe, [1, 2, 3, 4]);
            Require(GitHubInstallPolicy.DigestOfFile(probe) ==
                Convert.ToHexString(SHA256.HashData([1, 2, 3, 4])).ToLowerInvariant(),
                "下载下来的包要能算出 sha256 小写十六进制值，安装时记录的就是它。");
            Require(GitHubInstallPolicy.DigestOfFile(Path.Combine(root, "missing.zip")).Length == 0,
                "读不到文件时校验值只能是空串，不能抛异常中断安装。");

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
            // 纯素材包没有清单可核对，此时台账条目就是归属证明；这里只管目录本身合法且存在。
            var loose = Path.Combine(mods, "CardArtPack");
            Directory.CreateDirectory(loose);
            Require(GitHubInstallPolicy.UninstallTarget(mods, "CardArtPack", "") == loose &&
                GitHubInstallPolicy.UninstallTarget(mods, "Missing", "") == null &&
                GitHubInstallPolicy.UninstallTarget(mods, "../evil", "") == null,
                "没有清单的包按记录删除，但目录必须仍是合法片段且真的存在。");

            // Release 压缩包里可能有好几个 Mod：优先取根目录的那个，其次取与仓库同名的文件夹。
            var nested = Path.Combine(root, "SilentSkinMod");
            Require(GitHubPackageLayout.SelectIndex([nested, root], root, "SilentSkinMod") == 1 &&
                GitHubPackageLayout.SelectIndex([nested], root, "SilentSkinMod") == 0 &&
                GitHubPackageLayout.SelectIndex([nested], root, "Other") == 0 &&
                GitHubPackageLayout.SelectIndex([root + Path.DirectorySeparatorChar], root, "Other") == 0 &&
                GitHubPackageLayout.SelectIndex([], root, "Other") == -1,
                "整包里挑哪一个是皮肤包必须确定：根清单优先，其次同名的子目录，空包返回 -1。");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("GitHub install policy passed: zip guards, install path, version and package digest state, manifest version.");
    }
}
