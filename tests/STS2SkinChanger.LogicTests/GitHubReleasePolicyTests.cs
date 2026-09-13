using STS2SkinChanger.Core;

// The regression this file pins down: a release was read while it still carried no package (the
// author uploaded the zip 22 minutes after publishing it), that read was cached, and from then on
// every scan and install answered "This release has no downloadable zip package." - the package
// was never even downloaded, so nothing about the archive layout was ever reached.
internal static class GitHubReleasePolicyTests
{
    private const string WithoutPackage = """
    {"tag_name":"无","name":"无有","published_at":"2026-09-13T16:35:50Z","assets":[
      {"name":"WatcherShell.pck","size":7909060,"digest":"sha256:020179f7f11b9184a38b991126b3e68058f4c40adc189fc28b3800f8521e284d","browser_download_url":"https://github.com/gurio-wine/STS2-TestSkin/releases/download/%E6%97%A0/WatcherShell.pck"}]}
    """;

    private const string WithPackage = """
    {"tag_name":"无","name":"无有","published_at":"2026-09-13T16:35:50Z","assets":[
      {"name":"default.zip","size":7909196,"digest":"sha256:020179f7f11b9184a38b991126b3e68058f4c40adc189fc28b3800f8521e284d","browser_download_url":"https://github.com/gurio-wine/STS2-TestSkin/releases/download/%E6%97%A0/default.zip"}]}
    """;

    public static void Run()
    {
        void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }

        // 刷新问的是"有没有更新的包"，已读到的 tag 足够回答；扫描/安装问的是"包在哪"，缓存回答不了。
        Require(GitHubReleasePolicy.ReuseCachedRead("v0.1.4", packageRequested: false),
            "刷新必须复用已读到的 Release，否则每个仓库每轮都多花一次接口额度。");
        Require(!GitHubReleasePolicy.ReuseCachedRead("v0.1.4", packageRequested: true),
            "扫描/安装必须重新读实时 Release：作者可以在发布之后再补压缩包，缓存里的旧答案会让扫描永远失败。");
        Require(!GitHubReleasePolicy.ReuseCachedRead("", packageRequested: false) &&
            !GitHubReleasePolicy.ReuseCachedRead("", packageRequested: true),
            "没有缓存过的仓库，两种情况都要去读一次。");

        var entry = new GitHubSkinEntry("gurio-wine/STS2-TestSkin", "STS2-TestSkin", "", 0, default, "",
            GitHubRecognition.Unrecognized, "missing", [], true, "", "", 0);
        var before = GitHubReleasePolicy.WithRelease(entry, WithoutPackage);
        Require(before.ReleaseTag == "无" && before.AssetUrl.Length == 0 && before.AssetSize == 0,
            "只发 dll/pck 的 Release 要记下 tag，但不能凭空造出可下载的包。");
        Require(GitHubReleasePolicy.ReuseCachedRead(before.ReleaseTag, packageRequested: false),
            "这种没有包的 Release 在刷新时仍然复用记录，不会让每轮刷新都为它多花一次请求。");
        var after = GitHubReleasePolicy.WithRelease(before, WithPackage);
        Require(after.AssetUrl.EndsWith("/default.zip", StringComparison.Ordinal) && after.AssetSize == 7909196 &&
            after.AssetDigest == "020179f7f11b9184a38b991126b3e68058f4c40adc189fc28b3800f8521e284d",
            "作者后补压缩包后，重新读到的 Release 必须给出可下载的包与它的校验值。");
        Require(GitHubReleasePolicy.WithRelease(entry, null) == entry &&
            GitHubReleasePolicy.WithRelease(entry, "{").ReleaseTag.Length == 0,
            "读不到 Release 或响应损坏时保持原样，不能把仓库变成「没有包」。");

        Console.WriteLine("GitHub release policy passed: refresh reuses a cached read, scan/install re-reads, package added later is found.");
    }
}
