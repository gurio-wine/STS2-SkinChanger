using STS2SkinChanger.Core;
using System.Text.Json;

internal static class GitHubSkinCatalogTests
{
    public static void Run()
    {
        void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
        var repo = new GitHubRepoInfo(new GitHubRepoRef("qbyoffice", "SilentSkinMod"), "silent", 12, DateTimeOffset.Parse("2026-09-10T04:05:06Z"));
        var pending = GitHubSkinCatalog.FromRepo(repo);
        Require(pending.Recognition == GitHubRecognition.Pending && pending.Targets.Length == 0 &&
            pending.Id == SkinSourceIdentity.GitHubId("qbyoffice", "SilentSkinMod"),
            "搜索阶段先建立待识别条目，ID 与仓库一一对应。");
        Require(pending.ReleaseTag.Length == 0, "尚未查询 Release 时不能伪造 tag，否则会跳过识别。");

        var recognized = pending with { ReleaseTag = "v0.1.4", AssetUrl = "https://github.com/qbyoffice/SilentSkinMod/releases/download/v0.1.4/SilentSkinMod.zip",
            AssetSize = 16777216, Recognition = GitHubRecognition.Recognized, RestartRequired = true,
            Targets = [new("character", "silent")] };
        var state = new GitHubSkinCatalog();
        state.Merge([pending]);
        Require(state.Entries.Length == 1 && state.Entries[0].Recognition == GitHubRecognition.Pending, "搜索结果必须并入目录。");
        state.Merge([pending with { Stars = 20 }]);
        Require(state.Entries[0].Stars == 20, "同一 pushed_at 的重复搜索必须就地更新星标，不重复建条目。");
        state.Publish(recognized);
        state.Merge([pending with { Stars = 21 }]);
        Require(state.Entries[0].Targets.Length == 1 && state.Entries[0].ReleaseTag == "v0.1.4",
            "pushed_at 未变时，重新搜索不能丢掉已识别的结果。");
        state.Merge([pending with { PushedAt = DateTimeOffset.Parse("2026-09-12T00:00:00Z") }]);
        Require(state.Entries[0].Recognition == GitHubRecognition.Pending && state.Entries[0].Targets.Length == 0 &&
            state.Entries[0].AssetUrl.Length == 0,
            "pushed_at 变化后必须重新读取 sc.info，旧的类型标签与旧 Release 都不能继续挂着。");

        var path = Path.Combine(Path.GetTempPath(), "sc-gh-catalog-" + Guid.NewGuid().ToString("N"), "github_skins.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            state.Publish(recognized);
            state.Save(path, null);
            var restored = new GitHubSkinCatalog();
            restored.Restore(path);
            Require(restored.Entries.Length == 1 && restored.Entries[0].AssetSize == 16777216 &&
                restored.Entries[0].Targets.Single().Target == "silent", "目录缓存必须能跨会话完整恢复。");
            File.WriteAllText(path, """{"Format":99,"Entries":[]}""");
            Require(Throws(() => restored.Restore(path)), "未知格式的缓存必须拒绝，不能按半懂的数据继续跑。");
            File.WriteAllText(path, """{"Format":2,"Entries":[]}""");
            Require(Throws(() => restored.Restore(path)), "旧格式缓存记录的是下载扫出来的结果，必须整份丢弃后重新读取 sc.info。");
            File.WriteAllText(path, """{"Format":3,"Entries":[{"FullName":"../evil","Name":"x"}]}""");
            Require(Throws(() => restored.Restore(path)), "缓存里的仓库名同样要按不可信输入校验。");
            File.WriteAllText(path, "{");
            Require(Throws(() => restored.Restore(path)), "损坏缓存必须抛错，由调用方保留空目录并重新拉取。");

            // 简介随缓存一起保存：悬停只请求一次，之后离线也能显示。
            restored.Publish(recognized with { Readme = new string('x', GitHubReadmePolicy.MaxExcerptChars) });
            restored.Save(path, null);
            var withReadme = new GitHubSkinCatalog();
            withReadme.Restore(path);
            Require(withReadme.Entries[0].Readme.Length == GitHubReadmePolicy.MaxExcerptChars,
                "仓库简介必须跨会话恢复，避免每次悬停都花一次接口额度。");
            withReadme.Merge([pending with { PushedAt = DateTimeOffset.Parse("2026-09-13T00:00:00Z") }]);
            Require(withReadme.Entries[0].Readme.Length == 0,
                "仓库有了新提交，旧版本的简介必须作废重新读取。");
            File.WriteAllText(path, JsonSerializer.Serialize(new GitHubSkinState(GitHubSkinCatalog.Format,
                [recognized with { Readme = new string('x', GitHubReadmePolicy.MaxExcerptChars + 1) }])));
            Require(Throws(() => restored.Restore(path)), "超过长度上限的简介必须拒绝，缓存文件不能被撑大。");
            File.WriteAllText(path, JsonSerializer.Serialize(new GitHubSkinState(GitHubSkinCatalog.Format,
                [recognized with { Recognition = GitHubRecognition.Recognized, Targets = [] }])));
            Require(Throws(() => restored.Restore(path)), "标成已识别却没有类型标签的缓存必须拒绝。");
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }

        // 不同仓库才有不同的合成 ID；同一仓库的两次快照会得到同一个 ID。
        var newer = recognized with { FullName = "someone/NewerSkin", Name = "NewerSkin", Stars = 5,
            PushedAt = DateTimeOffset.Parse("2026-09-12T00:00:00Z") };
        Require(newer.Id != recognized.Id, "测试夹具必须用两个不同仓库，否则排序断言会退化成同一个条目。");
        var entries = new Dictionary<ulong, GitHubSkinEntry> { [recognized.Id] = recognized, [newer.Id] = newer };
        Require(GitHubSortPolicy.OrderIds([recognized.Id, newer.Id], entries, GitHubSort.Updated)[0] == newer.Id,
            "按最近更新排序时 pushed_at 新的在前。");
        Require(GitHubSortPolicy.OrderIds([recognized.Id, newer.Id], entries, GitHubSort.Stars)[0] == recognized.Id,
            "按星标排序时星标高的在前。");
        Require(GitHubSortPolicy.OrderIds([recognized.Id, newer.Id], entries, GitHubSort.Name)[0] == newer.Id &&
            GitHubSortPolicy.OrderIds([recognized.Id, newer.Id], entries, GitHubSort.Name, reverse: true)[0] == recognized.Id,
            "按名称排序及其反向必须真的反过来，且不依赖字典顺序。");
        Require(GitHubSortPolicy.Metric(recognized, GitHubSort.Stars).Contains("12") &&
            GitHubSortPolicy.Metric(recognized, GitHubSort.Name) == recognized.Name &&
            GitHubSortPolicy.Metric(null, GitHubSort.Stars) == "—",
            "指标文本要跟着排序键走：星标显示星数，名称排序显示仓库名。");
        Console.WriteLine("GitHub skin catalog passed: merge keeps recognition, cache round-trip, sorting.");
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (InvalidDataException) { return true; }
    }
}
