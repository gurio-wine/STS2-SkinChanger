using STS2SkinChanger.Core;

internal static class GitHubSkinPolicyTests
{
    private const string Search = """
    {"total_count":3,"items":[
      {"full_name":"gurio-wine/STS2-SkinChanger","name":"STS2-SkinChanger","description":"self",
       "stargazers_count":9,"pushed_at":"2026-09-13T01:02:03Z","fork":false,"owner":{"login":"gurio-wine"}},
      {"full_name":"qbyoffice/SilentSkinMod","name":"SilentSkinMod","description":"silent skin",
       "stargazers_count":12,"pushed_at":"2026-09-10T04:05:06Z","fork":false,"owner":{"login":"qbyoffice"}},
      {"full_name":"someone/forked-skin","name":"forked-skin","description":"fork",
       "stargazers_count":1,"pushed_at":"2026-09-09T00:00:00Z","fork":true,"owner":{"login":"someone"}}]}
    """;

    private const string Release = """
    {"tag_name":"v0.1.4","name":"v0.1.4 Release","published_at":"2026-09-10T04:05:06Z","assets":[
      {"name":"SilentSkinMod.zip","size":16777216,"browser_download_url":"https://github.com/qbyoffice/SilentSkinMod/releases/download/v0.1.4/SilentSkinMod.zip"},
      {"name":"checksums.txt","size":128,"browser_download_url":"https://github.com/qbyoffice/SilentSkinMod/releases/download/v0.1.4/checksums.txt"}]}
    """;

    public static void Run()
    {
        void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }

        var repos = GitHubSkinPolicy.ParseSearch(Search);
        Require(repos.Length == 1 && repos[0].Repo.FullName == "qbyoffice/SilentSkinMod",
            "主题列表必须排除本 Mod 仓库和 fork，只保留真正的第三方仓库。");
        Require(repos[0].Stars == 12 && repos[0].PushedAt == DateTimeOffset.Parse("2026-09-10T04:05:06Z"),
            "星标与 pushed_at 必须按搜索结果原样解析，用于排序和缓存比对。");
        Require(GitHubSkinPolicy.ParseSearch("{").Length == 0 && GitHubSkinPolicy.ParseSearch("").Length == 0,
            "损坏的搜索响应必须当成空列表，不能抛出中断整次刷新。");

        Require(GitHubSkinPolicy.IsAllowedHost("api.github.com") && GitHubSkinPolicy.IsAllowedHost("objects.githubusercontent.com") &&
            GitHubSkinPolicy.IsAllowedHost("opengraph.githubassets.com"),
            "接口、资产重定向与封面的主机必须在白名单内。");
        Require(GitHubSkinPolicy.IsAllowedHost("release-assets.githubusercontent.com"),
            "GitHub 现在把 Release 资产重定向到 release-assets.githubusercontent.com；漏掉它会让每一次下载都被拒绝。");
        Require(!GitHubSkinPolicy.IsAllowedHost("evil.example.com") && !GitHubSkinPolicy.IsAllowedHost("github.com.evil.example.com"),
            "非白名单主机，包括后缀伪装的域名，必须被拒绝。");
        Require(!GitHubSkinPolicy.TryParseFullName("owner/..", out _), "只有点号的仓库名不是合法仓库，必须被拒绝。");
        Require(!GitHubSkinPolicy.TryParseFullName("../evil", out _) && !GitHubSkinPolicy.TryParseFullName("a/b/c", out _),
            "缓存里的仓库名同样按不可信输入校验：路径段与多余分隔符都不能通过。");

        var release = GitHubSkinPolicy.ParseRelease(Release)!;
        Require(release.Tag == "v0.1.4" && release.Assets.Length == 2, "Release tag 与资产清单必须完整解析。");
        var asset = GitHubSkinPolicy.SelectAsset(release, "SilentSkinMod")!;
        Require(asset.Name == "SilentSkinMod.zip" && GitHubSkinPolicy.IsAllowedHost(new Uri(asset.Url).Host),
            "资产必须选 zip，且下载地址主机在白名单内。");
        Require(GitHubSkinPolicy.SelectAsset(new GitHubReleaseInfo("v1", "", default, []), "x") == null,
            "没有任何 zip 资产的 Release 必须判为无法安装。");
        Require(GitHubSkinPolicy.SelectAsset(release with { Assets = [asset with { Size = GitHubSkinPolicy.MaxAssetBytes + 1 }] }, "SilentSkinMod") == null,
            "超过体积上限的资产不能下载。");
        Require(GitHubSkinPolicy.SelectAsset(release, "SilentSkinMod")!.Url.StartsWith("https://github.com/"),
            "只接受 https 的资产地址。");

        Require(GitHubSkinPolicy.CoverUrl(repos[0].Repo) == "https://opengraph.githubassets.com/1/qbyoffice/SilentSkinMod",
            "封面地址必须走无接口限额的 opengraph 主机。");
        Require(GitHubSkinPolicy.SearchUri(1).Host == "api.github.com" &&
            GitHubSkinPolicy.SearchUri(1).Query.Contains("topic%3Asts2-sc-mod"),
            "搜索请求必须打在 topic 查询上。");
        // README 走 API 端点：文件名大小写和默认分支由 GitHub 解析，raw 主机做不到。
        Require(GitHubSkinPolicy.ReadmeUri(repos[0].Repo).AbsoluteUri == "https://api.github.com/repos/qbyoffice/SilentSkinMod/readme" &&
            GitHubSkinPolicy.IsAllowedHost(GitHubSkinPolicy.ReadmeUri(repos[0].Repo).Host),
            "仓库简介的地址必须落在白名单主机上，否则请求会被出网校验拒掉。");

        var recognized = SkinSourceIdentity.GitHubId("o", "r");
        Require(GitHubSkinPolicy.KeepWithoutTargets(recognized, 0, "") && !GitHubSkinPolicy.KeepWithoutTargets(recognized, 0, "character"),
            "未识别的仓库只在没有类型筛选时列出，选了类型就不能假装它属于该类型。");
        Require(!GitHubSkinPolicy.KeepWithoutTargets(recognized, 2, "") && !GitHubSkinPolicy.KeepWithoutTargets(3787302680UL, 0, ""),
            "已识别条目与 Steam 条目不受这条规则影响。");
        Console.WriteLine("GitHub skin policy passed: search filter, host allowlist, release assets, cover, unrecognized listing.");
    }
}
