using STS2SkinChanger;
using STS2SkinChanger.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net;
using System.IO.Compression;

internal static class WorkshopSubmissionIntegrityTests
{
    internal static async Task Run()
    {
        if (typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.WorkshopSubmissionV2") == null)
            throw new InvalidOperationException("缺少包含名称、完整分片组和可定位错误的投稿协议。");
        var item = new WorkshopCatalogItem(456, Enumerable.Range(0, 150).Select(i => new WorkshopTarget("monster",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(i.ToString()))))).ToArray());
        var codes = WorkshopSubmissionV2.Encode(item, "中文 SCM2 模组", "1.0.3.4", "0.111.0");
        Require(codes.Length > 1 && codes.All(c => c.Length <= 1800 && c.StartsWith("SCM2 \"中文 SCM2 模组\" 456 ")), "码头必须保留可读名称、ID和片号。");
        var partial = Read(codes[..^1]);
        Require(partial.Candidates.Length == 0 && partial.Issues.Any(i => i.Error == WorkshopCodeError.MissingParts && i.Missing.SequenceEqual(new[] { codes.Length })), "缺片不能收录，须显示具体缺少哪片。");
        var complete = Read(codes.Reverse().Concat(codes).ToArray());
        Require(complete.Candidates.Length == 1 && complete.Issues.Length == 0 && complete.Candidates[0].Payload.Item.Targets.Length == 150, "乱序/重复/名称含SCM时仍必须正确完整合并。");
        Require(complete.Candidates[0].Sources.Any(s => s.Url.EndsWith("#c100")), "原帖定位必须随信息码保留。");
        var lookup = new Dictionary<ulong, WorkshopIdentity> { [456] = new(2868840, "中文 SCM2 模组") };
        var valid = WorkshopSubmissionIntegrity.Verify(complete, lookup, WorkshopCommunityState.Empty);
        Require(valid.Entries.Single().Item.Id == 456, "名称ID及所属游戏通过后才可收录。");
        var mismatch = WorkshopSubmissionIntegrity.Verify(complete, new Dictionary<ulong, WorkshopIdentity> { [456] = new(2868840, "另外的模组") }, valid);
        Require(mismatch.Entries.Length == 0 && mismatch.Issues.Single().ActualName == "另外的模组" && mismatch.Issues.Single().Error == WorkshopCodeError.NameMismatch, "名称不符不能加载或沿用同名旧缓存。");
        var keep = WorkshopSubmissionIntegrity.Verify(partial, lookup, valid);
        Require(keep.Entries.Length == 1 && keep.Issues.Any(i => i.Error == WorkshopCodeError.MissingParts), "坏的新投稿不能覆盖仍通过名称核验的完整旧条目。");
        var single = WorkshopSubmissionV2.Encode(new(789, [new("character", "silent")]), "Normal", "1", "0.111").Single();
        var bad = single[..^1] + (single[^1] == '0' ? '1' : '0');
        Require(Read([bad]).Issues.Single() is { Id: 789, Total: 1, Sources: [{ Part: 1 }] }, "校验失败仍应保留可解析的物品ID与片号。");
        Require(Read([bad, single.Replace("SCM2 ", "SCM99 ")]).Issues.Select(i => i.Error).ToHashSet().SetEquals(new[] { WorkshopCodeError.Checksum, WorkshopCodeError.Version }), "坏校验及未知版本必须列出，不能静默吞掉。");
        var missingName = Resign(single.Replace("\"Normal\"", "\"\""));
        Require(Read([missingName]).Issues.Single() is { Id: 789, Error: WorkshopCodeError.MissingName }, "缺名也必须能按ID定位。");
        var legacy = WorkshopSubmissionCode.Encode([new(100, [new("character", "silent")])], "1", "0.111").Single();
        Require(Read([legacy]).Issues.Single() is { Id: 100, Error: WorkshopCodeError.Legacy }, "旧码无名称/完整性资料，不能悄悄绕过校验。");
        var mixed = Read([bad, single]);
        Require(mixed.Candidates.Length == 1 && mixed.Issues.Length == 1, "一个坏码不能阻止独立的好码。");
        var altered = codes[0][..codes[0].LastIndexOf('.')];
        altered = altered[..^1] + (altered[^1] == 'A' ? 'B' : 'A');
        var conflict = Read(codes.Append(Resign(altered + ".00000000")).ToArray());
        Require(conflict.Candidates.Length == 0 && conflict.Issues.Any(i => i.Error == WorkshopCodeError.Conflict), "同组同片号不同内容必须拒绝。");
        Require(WorkshopSubmissionIntegrity.Verify(complete, new Dictionary<ulong, WorkshopIdentity> { [456] = new(1, "中文 SCM2 模组") }, valid)
            is { Entries.Length: 0, Issues: [{ Error: WorkshopCodeError.WrongGame }] }, "其它游戏不能沿用已验证缓存。");
        Require(WorkshopSubmissionIntegrity.Verify(complete, new Dictionary<ulong, WorkshopIdentity>(), valid)
            is { Entries.Length: 0, Issues: [{ Error: WorkshopCodeError.Unavailable }] }, "查不到物品必须可诊断且不导入。");
        var repaired = WorkshopSubmissionIntegrity.Verify(complete, lookup, keep);
        Require(repaired.Entries.Length == 1 && repaired.Issues.Length == 0, "原帖补齐后，刷新须自动清除缺片错误。");
        var sourcePosts = await WorkshopDiscussionSource.ReadAllPosts((uri, _) => Task.FromResult(
            Page(codes.Length, uri.Query.Length == 0 ? 0 : int.Parse(uri.Query[5..]) - 1, codes)), null, default);
        Require(sourcePosts.Count(p => p.Reply == 0) == 1, "重复出现在每页的主楼不能被当成新投稿，覆盖较新回复。");
        var fromHtml = WorkshopSubmissionV2.Read(sourcePosts);
        Require(fromHtml.Candidates.Length == 1 && fromHtml.Issues.Length == 0 &&
            fromHtml.Candidates[0].Sources.Any(s => s.Page == 2 && s.Reply == 2 && s.Url.EndsWith("?ctp=2#c101")),
            "Steam跨页分片、HTML实体和回复定位必须保留。");
        var filter = new WorkshopSubscriptionFilter(); filter.Select("code_errors"); filter.BeginAction(456);
        Require(!filter.Matches(456), "码错误不能混入正在订阅的普通条目。");
        var problem = WorkshopCodeDiagnostics.Group(partial.Issues.Concat(partial.Issues)).Single();
        Require(problem.Sources.Length == codes.Length - 1, "重复问题不应生成重复来源。");
        var report = WorkshopCodeDiagnostics.Report(problem, "zhs");
        Require(report.Contains("[SCM-MissingParts]") && report.Contains(codes[0]) && report.Contains("#c100") && report.Contains("缺少："),
            "复制详情必须包含错误类型、缺片、原码及原帖链接。");
        foreach (var url in new[] { WorkshopDiscussionSource.Url, WorkshopDiscussionSource.Url + "?ctp=2#c123" })
            Require(WorkshopCodeDiagnostics.SafeSource(url), "有效帖子定位链接被拒绝。");
        foreach (var url in new[] { "file:///C:/a", "steam://install/123", WorkshopDiscussionSource.Url + "?next=https://evil.example", WorkshopDiscussionSource.Url.Replace("steamcommunity.com", "steamcommunity.com.evil.example"), WorkshopDiscussionSource.Url.Replace("https:", "http:") })
            Require(!WorkshopCodeDiagnostics.SafeSource(url), "投稿不得指定任意跳转地址。");
        foreach (var language in new[] { "zhs", "zht", "eng", "deu", "esp", "spa", "fra", "ita", "jpn", "kor", "pol", "ptb", "rus", "tha", "tur" })
            for (var i = 0; i < Enum.GetValues<WorkshopCodeUi>().Length + Enum.GetValues<WorkshopCodeError>().Length; i++)
                Require(!string.IsNullOrWhiteSpace(string.Format(WorkshopCodeErrorText.ForLanguage(language, i), 1, 2)), "错误界面本地化缺失。");
        await CacheValidation(keep);
        var payload = new WorkshopNamedPayload(2, 2868840, "Test", "1", "0.111", new(999, [new("cards", "silent")]));
        Require(Read([RawCode(JsonSerializer.Serialize(payload with { App = 1 }))]).Issues.Single().Error == WorkshopCodeError.WrongGame, "码内所属游戏同样需要核验。");
        Require(Read([RawCode(JsonSerializer.Serialize(payload with { Item = new(999, [new("script", "execute")]) }))]).Issues.Single().Error == WorkshopCodeError.InvalidData, "未知分类不是可执行指令。");
        Require(Read([RawCode(new string(' ', 200000))]).Issues.Single().Error == WorkshopCodeError.TooLarge, "新版同样必须限制解压体积。");
        var newer = WorkshopSubmissionV2.Encode(new(456, [new("cards", "silent")]), "中文 SCM2 模组", "2", "0.111");
        var updated = WorkshopSubmissionIntegrity.Verify(Read(codes.Concat(newer).ToArray()), lookup, valid);
        Require(updated.Entries.Single().Item.Targets is [{ Kind: "cards" }], "同物品多个完整投稿组取最新组，不能叠加过时分类。");
        Console.WriteLine("Submission integrity passed: complete groups, readable names, source locations, bad-code diagnostics and Steam identity checks.");
    }
    private static string Page(int total, int start, string[] codes) =>
        "<div class=\"forum_op\">ordinary text</div><div class=\"commentthread_comment_text\" id=\"comment_content_" + (100 + start) + "\"><div>" +
        WebUtility.HtmlEncode(codes[start]) + "</div><br>plain text</div><script>InitializeCommentThread(\"ForumTopic\",\"thread\",{\"feature2\":\"" +
        WorkshopDiscussionSource.Topic + "\",\"total_count\":" + total + ",\"start\":" + start + ",\"pagesize\":1},10);</script>";
    private static async Task CacheValidation(WorkshopCommunityState state)
    {
        var root = Directory.CreateTempSubdirectory("sc-integrity-");
        var path = Path.Combine(root.FullName, "catalog.json");
        try
        {
            var catalog = new WorkshopCommunityCatalog(); await catalog.Replace(() => Task.FromResult(state), path);
            var restored = new WorkshopCommunityCatalog(); await restored.Restore(path);
            Require(restored.State.Issues.Single().Missing.SequenceEqual(state.Issues.Single().Missing), "错误分片信息须缓存。");
            var before = await File.ReadAllTextAsync(path);
            var invalid = state with { Issues = [state.Issues.Single() with { Sources = null! }] };
            try { await catalog.Replace(() => Task.FromResult(invalid), path); throw new Exception("空错误来源数组被写入缓存。"); }
            catch (InvalidDataException) { }
            Require(await File.ReadAllTextAsync(path) == before, "无效缓存不能覆盖有效缓存。");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Format = 2, Entries = state.Entries, Issues = new object?[] { null } }));
            try { await restored.Restore(path); throw new Exception("空错误项被还原。"); } catch (InvalidDataException) { }
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(state.Entries.Select(e => e.Item)));
            try { await restored.Restore(path); throw new Exception("旧版无名称缓存被重新加载。"); } catch (Exception ex) when (ex is InvalidDataException or JsonException) { }
        }
        finally { root.Delete(true); }
    }
    private static WorkshopCodeRead Read(string[] codes) => WorkshopSubmissionV2.Read(codes.Select((c, i) => new WorkshopCodePost(c, WorkshopDiscussionSource.Url + "#c" + (100 + i), 1, i + 1)));
    private static string Resign(string code)
    { var body = code[..code.LastIndexOf('.')]; return body + "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))[..8]; }
    private static string RawCode(string json)
    {
        using var stream = new MemoryStream();
        using (var zip = new BrotliStream(stream, CompressionLevel.Optimal, true)) zip.Write(Encoding.UTF8.GetBytes(json));
        var packed = stream.ToArray();
        var raw = "SCM2 \"Test\" 999 " + Convert.ToHexString(SHA256.HashData(packed))[..24] + " 1/1 " +
            Convert.ToBase64String(packed).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Resign(raw + ".00000000");
    }
    private static void Require(bool value, string error) { if (!value) throw new InvalidOperationException(error); }
}
