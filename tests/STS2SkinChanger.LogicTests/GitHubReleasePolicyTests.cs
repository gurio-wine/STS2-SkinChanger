using STS2SkinChanger.Core;

// What this file pins down, in the order the two bugs appeared.
// 1. A release was read while it still carried no package (the author uploaded the zip 22 minutes
//    after publishing it), that read was cached, and from then on every scan and install answered
//    "This release has no downloadable zip package." - the package was never even downloaded, so
//    nothing about the archive layout was ever reached.
// 2. The refresh reused that cached read for every repository it had read once, so a package the
//    author replaced under an unchanged tag - or a release published without a new commit - was
//    never looked at again: the card kept answering from a digest nobody had re-read.
internal static class GitHubReleasePolicyTests
{
    private const string DigestA = "020179f7f11b9184a38b991126b3e68058f4c40adc189fc28b3800f8521e284d";
    private const string DigestB = "120179f7f11b9184a38b991126b3e68058f4c40adc189fc28b3800f8521e284d";

    private const string WithoutPackage = """
    {"tag_name":"无","name":"无有","published_at":"2026-09-13T16:35:50Z","assets":[
      {"name":"WatcherShell.pck","size":7909060,"digest":"sha256:020179f7f11b9184a38b991126b3e68058f4c40adc189fc28b3800f8521e284d","browser_download_url":"https://github.com/gurio-wine/STS2-TestSkin/releases/download/%E6%97%A0/WatcherShell.pck"}]}
    """;

    private const string WithPackage = """
    {"tag_name":"无","name":"无有","published_at":"2026-09-13T16:35:50Z","assets":[
      {"name":"default.zip","size":7909196,"digest":"sha256:020179f7f11b9184a38b991126b3e68058f4c40adc189fc28b3800f8521e284d","browser_download_url":"https://github.com/gurio-wine/STS2-TestSkin/releases/download/%E6%97%A0/default.zip"}]}
    """;

    // The same release, the same tag, a different package behind it.
    private const string ReplacedPackage = """
    {"tag_name":"无","name":"无有","published_at":"2026-09-13T16:35:50Z","assets":[
      {"name":"default.zip","size":7909196,"digest":"sha256:120179f7f11b9184a38b991126b3e68058f4c40adc189fc28b3800f8521e284d","browser_download_url":"https://github.com/gurio-wine/STS2-TestSkin/releases/download/%E6%97%A0/default.zip"}]}
    """;

    private const string AssetUrl =
        "https://github.com/gurio-wine/STS2-TestSkin/releases/download/%E6%97%A0/default.zip";

    public static void Run()
    {
        void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }

        var cached = new GitHubSkinEntry("gurio-wine/STS2-TestSkin", "STS2-TestSkin", "", 0, default, "",
            GitHubRecognition.Recognized, "", [], true, "无", AssetUrl, 7909196, DigestA);

        // The refresh asks "is the package published now still the one I installed?", and only a live
        // read answers it: an author can replace the file under a tag that never changes, or publish a
        // release without pushing a commit. Every installed repository is therefore read every round,
        // however complete the cache looks.
        Require(GitHubReleasePolicy.ReleaseChecks([cached], _ => true, 10).Length == 1,
            "已安装且缓存里已有 tag 的仓库仍然必须重读 Release：换包不换 tag 时没有别的信号能发现更新。");
        Require(GitHubReleasePolicy.ReleaseChecks([cached], _ => false, 10).Length == 0,
            "没装过的仓库不查 Release：没有可比较的安装，读回来的东西回答不了任何问题。");

        var many = Enumerable.Range(0, 12).Select(index => cached with
        {
            FullName = $"owner/repo{index}",
            PushedAt = new DateTimeOffset(2026, 9, 13, 0, 0, index, TimeSpan.Zero)
        }).ToArray();
        var capped = GitHubReleasePolicy.ReleaseChecks(many, _ => true, 10);
        Require(capped.Length == 10 && capped[0].FullName == "owner/repo11" && capped[^1].FullName == "owner/repo2",
            "一轮刷新最多查 10 个已安装仓库，最近有提交的先查，其余留到下一轮。");

        var empty = new GitHubSkinEntry("gurio-wine/STS2-TestSkin", "STS2-TestSkin", "", 0, default, "",
            GitHubRecognition.Unrecognized, "missing", [], true, "", "", 0);
        var before = GitHubReleasePolicy.WithRelease(empty, WithoutPackage);
        Require(before.ReleaseTag == "无" && before.AssetUrl.Length == 0 && before.AssetSize == 0,
            "只发 dll/pck 的 Release 要记下 tag，但不能凭空造出可下载的包。");
        var added = GitHubReleasePolicy.WithRelease(before, WithPackage);
        Require(added.AssetUrl.EndsWith("/default.zip", StringComparison.Ordinal) && added.AssetSize == 7909196 &&
            added.AssetDigest == DigestA,
            "作者后补压缩包后，重新读到的 Release 必须给出可下载的包与它的校验值。");
        var replaced = GitHubReleasePolicy.WithRelease(added, ReplacedPackage);
        Require(replaced.ReleaseTag == added.ReleaseTag && replaced.AssetDigest == DigestB,
            "同一个 tag 下换掉附件，读回来必须是新的校验值，不能因为 tag 没变就当作没变。");
        Require(GitHubInstallPolicy.Classify("无", DigestA, replaced.ReleaseTag, replaced.AssetDigest) == GitHubInstallState.Updatable,
            "同一个 tag 下重发的包，刷新读回后必须判为可更新。");
        Require(GitHubReleasePolicy.WithRelease(empty, null) == empty &&
            GitHubReleasePolicy.WithRelease(empty, "{").ReleaseTag.Length == 0,
            "读不到 Release 或响应损坏时保持原样，不能把仓库变成「没有包」。");

        Console.WriteLine("GitHub release policy passed: every installed repository is re-read, " +
            "a package replaced under the same tag reads as an update.");
    }
}
