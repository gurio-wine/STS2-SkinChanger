using STS2SkinChanger.Core;

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

        var recognized = pending with { ReleaseTag = "v0.1.4", PackageId = "KaguyaSilentRavenSkin", PackageName = "Silent",
            PackageVersion = "0.1.4", Recognition = GitHubRecognition.Recognized, RestartRequired = true,
            Targets = [new("character", "silent")] };
        var state = new GitHubSkinCatalog();
        state.Merge([pending]);
        Require(state.Entries.Length == 1 && state.Entries[0].Recognition == GitHubRecognition.Pending, "搜索结果必须并入目录。");
        state.Merge([pending with { Stars = 20 }]);
        Require(state.Entries[0].Stars == 20, "同一 pushed_at 的重复搜索必须就地更新星标，不重复建条目。");
        state.Publish(recognized);
        state.Merge([pending with { Stars = 21 }]);
        Require(state.Entries[0].Targets.Length == 1 && state.Entries[0].PackageId == "KaguyaSilentRavenSkin",
            "pushed_at 未变时，重新搜索不能丢掉已识别的结果。");
        state.Merge([pending with { PushedAt = DateTimeOffset.Parse("2026-09-12T00:00:00Z") }]);
        Require(state.Entries[0].Recognition == GitHubRecognition.Pending && state.Entries[0].Targets.Length == 0,
            "pushed_at 变化后必须重新识别，旧的类型标签不能继续挂着。");

        var path = Path.Combine(Path.GetTempPath(), "sc-gh-catalog-" + Guid.NewGuid().ToString("N"), "github_skins.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            state.Publish(recognized);
            state.Save(path, null);
            var restored = new GitHubSkinCatalog();
            restored.Restore(path);
            Require(restored.Entries.Length == 1 && restored.Entries[0].PackageVersion == "0.1.4" &&
                restored.Entries[0].Targets.Single().Target == "silent", "目录缓存必须能跨会话完整恢复。");
            File.WriteAllText(path, """{"format":99,"entries":[]}""");
            Require(Throws(() => restored.Restore(path)), "未知格式的缓存必须拒绝，不能按半懂的数据继续跑。");
            File.WriteAllText(path, """{"format":1,"entries":[{"fullName":"../evil","name":"x"}]}""");
            Require(Throws(() => restored.Restore(path)), "缓存里的仓库名同样要按不可信输入校验。");
            File.WriteAllText(path, "{");
            Require(Throws(() => restored.Restore(path)), "损坏缓存必须抛错，由调用方保留空目录并重新拉取。");
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
            GitHubSortPolicy.Metric(null, GitHubSort.Stars) == "—",
            "指标文本要显示星标，缺失条目显示占位符。");
        Console.WriteLine("GitHub skin catalog passed: merge keeps recognition, cache round-trip, sorting.");
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (InvalidDataException) { return true; }
    }
}
