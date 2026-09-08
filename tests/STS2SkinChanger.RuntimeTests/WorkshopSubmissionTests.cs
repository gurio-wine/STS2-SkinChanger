using System.Reflection;
using STS2SkinChanger;
using STS2SkinChanger.Core;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Pck;

internal static class WorkshopSubmissionTests
{
    internal static async Task Run()
    {
        var code = typeof(Entry).Assembly.GetType("STS2SkinChanger.Core.WorkshopSubmissionCode");
        if (code == null) throw new InvalidOperationException("缺少可独立解码的批量模组投稿码。");
        await RunImplementation();
    }
    private static async Task RunImplementation()
    {
        var items = Enumerable.Range(1, 180).Select(i => new WorkshopCatalogItem((ulong)i,
            [new("character", "silent"), new("cards", "silent")], i % 2 == 0)).ToArray();
        var lines = WorkshopSubmissionCode.Encode(items, "1.0.3.3", "0.111.0");
        Require(lines.Length > 1 && lines.All(l => l.Length <= 1800), "批量投稿必须拆成可独立粘贴的短码。");
        var roundtrip = WorkshopSubmissionCode.ReadPosts(lines.Reverse().Concat(lines));
        Require(roundtrip.Length == 180 && roundtrip[0].Id == 1 && roundtrip[^1].Id == 180 &&
            !roundtrip[0].RestartRequired && roundtrip[1].RestartRequired && roundtrip[0].Targets.Length == 2,
            "乱序、重复留言不能丢失分类、重启标签或生成重复物品。");
        Require(!string.Join("", lines.Select(l => JsonSerializer.Serialize(WorkshopSubmissionCode.Decode(l)))).Contains("C:\\"), "不得包含本地路径。");
        var large=new WorkshopCatalogItem(200,Enumerable.Range(0,150).Select(i=>new WorkshopTarget("monster",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(i.ToString()))))).ToArray(),true);
        var largeCodes=WorkshopSubmissionCode.Encode([large],"1","0.111");
        Require(largeCodes.Length>1 && WorkshopSubmissionCode.ReadPosts(largeCodes).Single().Targets.Length==150,
            "一个大型Mod也可以跨多条码，不因分类多而丢弃整批扫描。");
        var corrupted = lines[0][..^1] + (lines[0][^1] == '0' ? '1' : '0');
        Require(WorkshopSubmissionCode.ReadPosts([corrupted, "SCM99.abc.12345678"]).Length == 0, "损坏或未知格式不能导入。");
        var raw = JsonSerializer.Serialize(new WorkshopSubmission(1, 1, "1.0.3.3", "0.111.0", [items[0]]));
        Require(WorkshopSubmissionCode.ReadPosts([RawCode(raw)]).Length == 0, "其它游戏投稿不能被接纳。");
        raw = JsonSerializer.Serialize(new WorkshopSubmission(1, 2868840, "1", "0.111", [new(1,[new("script", "anything")])]));
        Require(WorkshopSubmissionCode.ReadPosts([RawCode(raw)]).Length == 0, "未知分类不能作为指令执行。");
        Require(WorkshopSubmissionCode.ReadPosts([RawCode(new string(' ', 200000))]).Length == 0, "压缩炸弹必须在解压上限拒绝。");
        var merged = WorkshopSubmissionCode.Merge([new(1,[new("character","regent")],true)], [items[0],items[1]]);
        Require(merged.Length == 2 && merged[0].Targets.Single().Target == "regent" && merged[0].RestartRequired,
            "公共留言不能覆盖内置清单的人工审核结果。");
        Require(WorkshopSubmissionCode.Merge([items[1],items[0]],[]).SequenceEqual(new[]{items[1],items[0]}),
            "没有投稿时不能重排内置条目或标签。");
        var code = WorkshopSubmissionCode.Encode([items[0]], "1", "0.111").Single();
        Require(WorkshopSubmissionCode.ReadPosts([corrupted,"SCM99.abc.12345678",code]).Single().Id==1,"无效投稿不能阻止其它有效投稿。");
        var html = Page(2,0,1,code);
        var parsed = WorkshopDiscussionSource.Parse(html);
        Require(WorkshopDiscussionSource.Parse("<div>InitializeCommentThread(\"ForumTopic\",\"fake\",{broken</div>"+html).Total==2,
            "留言中的伪分页声明不能作为页面脚本读取。");
        Require(parsed.Posts.Length == 2 && WorkshopSubmissionCode.ReadPosts(parsed.Posts).Length == 1,
            "只读取帖子内容；忽略脚本与页面其它区域里的伪造投稿。");
        var requests = new List<string>();
        var read = await WorkshopDiscussionSource.ReadAll((uri, _) =>
        {
            requests.Add(uri.Query);
            return Task.FromResult(uri.Query.Length == 0 ? html : Page(2,1,1,WorkshopSubmissionCode.Encode([items[1]],"1","0.111").Single()));
        }, null, default);
        Require(read.Select(i=>i.Id).SequenceEqual(new ulong[] { 1,2 }) && requests.SequenceEqual(new[] { "", "?ctp=2" }),
            "必须读取所有分页，而不是仅主楼/第一页。");
        try
        {
            await WorkshopDiscussionSource.ReadAll((uri,_) => Task.FromResult(html),null,default);
            throw new Exception("分页返回第一页时被误认为成功。");
        }
        catch (InvalidDataException) { }
        try { WorkshopDiscussionSource.Parse("<html>Sign in</html>"); throw new Exception("登录/错误页面被误认为空清单。"); }
        catch (InvalidDataException) { }
        try { WorkshopDiscussionSource.Parse(Page(2,0,1,code).Replace("commentthread_comment_text","missing")); throw new Exception("缺失的回复被当成完整分页。"); }
        catch (InvalidDataException) { }
        await CacheAndLanguages(items);
        await MetadataDelta();
        ScanPackages();
        Console.WriteLine("Workshop submissions passed: independent batch codes, validation, bounded expansion, built-in precedence and complete pagination.");
    }

    internal static void Audit(string gamePack,string directory,ulong id)
    {
        var cards=new List<CardCatalogEntry>();
        using(var archive=PckArchive.Open(gamePack))
            foreach(var path in archive.Paths)
            {
                var source=path.EndsWith(".import") ? path[..^7] : path.EndsWith(".remap") ? path[..^6] : path;
                var marker=new[]{"/card_portraits/","/card_atlas.sprites/"}.FirstOrDefault(source.Contains);
                if(marker==null)continue;
                var tail=source[(source.IndexOf(marker,StringComparison.Ordinal)+marker.Length)..];
                if(!tail.Contains('/'))continue;
                var group=tail.Split('/')[0];
                cards.Add(new(Path.GetFileNameWithoutExtension(source),source,group,group,group));
            }
        var result=WorkshopSubmissionScanner.Scan(new(id,Path.GetFileName(directory),directory),gamePack,cards.Distinct().ToArray(),"0.111.0",new Dictionary<string,string>());
        Require(result.Item!=null,result.Error);
        var codes=WorkshopSubmissionCode.Encode([result.Item!],"1.0.3.3","0.111.0");
        Console.WriteLine($"Read-only submission audit: {id}; targets={JsonSerializer.Serialize(result.Item!.Targets)}; restart={result.Item.RestartRequired}; code lengths={string.Join(',',codes.Select(c=>c.Length))}");
    }

    private static void ScanPackages()
    {
        var root=Directory.CreateTempSubdirectory("sc-submission-scan-");
        try
        {
            var source=Path.Combine(root.FullName,"2868840","123");
            Directory.CreateDirectory(source);
            var game=Path.Combine(root.FullName,"game.pck");
            var skin=Path.Combine(source,"skin.pck");
            const string resource="res://animations/characters/necrobinder/model.tres";
            var files=new Dictionary<string,byte[]> { [resource]=Encoding.UTF8.GetBytes("[gd_resource type=\"Resource\" format=3]\n[resource]\n") };
            PckArchive.Write(game,files); PckArchive.Write(skin,files);
            var manifest=Path.Combine(source,"skin.json");
            const string original="""{"id":"skin","name":"Test skin","has_dll":false,"has_pck":true,"affects_gameplay":false}""";
            File.WriteAllText(manifest,original);
            var scan=WorkshopSubmissionScanner.Scan(new(123,"Test",source),game,[],"0.111.0",new Dictionary<string,string>());
            Require(scan.Item is { RestartRequired:false } && scan.Item.Targets.Contains(new WorkshopTarget("character","necrobinder")),"真实PCK扫描必须生成当前包的角色分类，并用完整资源检查决定重启标签。");
            Require(File.ReadAllText(manifest)==original,"扫描不能修改作者文件。");
            var custom=Path.Combine(source,"custom.pck");
            File.Move(skin,custom);
            File.WriteAllText(manifest,original.Replace("\"has_pck\":true","\"has_pck\":true,\"pck_name\":\"custom\""));
            Require(WorkshopSubmissionScanner.ReadDescriptors(source).Single().PckPath==custom,"显式资源包名必须使用作者声明。");
            File.Move(custom,skin); File.WriteAllText(manifest,original);
            files["res://unmanaged/settings.txt"]=[1]; PckArchive.Write(skin,files);
            scan=WorkshopSubmissionScanner.Scan(new(123,"Test",source),game,[],"0.111.0",new Dictionary<string,string>());
            Require(scan.Item is { RestartRequired:true },"未被覆盖的内容不能承诺免重启。");
            File.WriteAllText(manifest,original.Replace("\"affects_gameplay\":false","\"affects_gameplay\":true"));
            Require(WorkshopSubmissionScanner.ReadDescriptors(source).Length==0,"玩法Mod不能仅因有PCK而成为投稿皮肤候选。");
        }
        finally { root.Delete(true); }
    }

    private static async Task MetadataDelta()
    {
        var session=new WorkshopCatalogSession<string>();
        var calls=new List<ulong[]>();
        async Task<Dictionary<ulong,string>> Load(ulong[] ids)
        { calls.Add(ids); await Task.Yield(); return ids.ToDictionary(i=>i,i=>"name"+i); }
        await Task.WhenAll(session.Get("english",[1,2],Load),session.Get("english",[1,2],Load));
        await session.Get("english",[1,2,3],Load);
        await session.Get("english",[3],Load);
        Require(calls.Count==2 && calls[0].SequenceEqual(new ulong[]{1,2}) && calls[1].SequenceEqual(new ulong[]{3}),
            "社区清单新增只查询新物品，翻页/筛选/重复刷新不重查已有物品。");
        Require(session.Cached("english").Count==3 && session.Cached("schinese").Count==0,"按语言缓存并保留已有资料。");
    }

    private static async Task CacheAndLanguages(WorkshopCatalogItem[] items)
    {
        var directory=Path.Combine(Path.GetTempPath(),"sc-submission-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path=Path.Combine(directory,"catalog.json");
        try
        {
            var catalog=new WorkshopCommunityCatalog();
            await catalog.Replace(()=>Task.FromResult(new[]{items[0]}),i=>Task.FromResult(i),path);
            var saved=await File.ReadAllTextAsync(path);
            try { await catalog.Replace(()=>throw new IOException("page two offline"),i=>Task.FromResult(i),path); throw new Exception("Load failure was ignored."); }
            catch(IOException) { }
            try { await catalog.Replace(()=>Task.FromResult(new[]{items[1]}),_=>throw new IOException("Steam verification offline"),path); throw new Exception("Verification failure was ignored."); }
            catch(IOException) { }
            Require(catalog.Items.Single().Id==1 && await File.ReadAllTextAsync(path)==saved,"刷新失败不能丢失内存或磁盘的完整清单。");
            var restored=new WorkshopCommunityCatalog();
            await restored.Restore(path);
            Require(restored.Items.Single().Id==1,"离线启动须恢复上次清单。");
            foreach(var language in new[]{"zhs","zht","eng","deu","esp","spa","fra","ita","jpn","kor","pol","ptb","rus","tha","tur"})
                foreach(var key in Enum.GetValues<SubmissionText>())
                    Require(!string.IsNullOrWhiteSpace(string.Format(WorkshopSubmissionText.ForLanguage(language,key),1,2,"mod")),"投稿界面本地化缺失。");
        }
        finally { Directory.Delete(directory,true); }
    }

    private static string Page(int total, int start, int size, string code) =>
        "<div class=\"other\">SCM1.bad.12345678</div><script>var unused='SCM1.bad.12345678';</script>" +
        "<div class=\"forum_op\"><div>Submission &amp; skin</div></div>" +
        "<div class=\"commentthread_comment_text\"><span>" + code + "</span></div>" +
        "<script>InitializeCommentThread(\"ForumTopic\",\"thread\",{\"feature2\":\"592940620292752301\",\"total_count\":" + total +
        ",\"start\":" + start + ",\"pagesize\":" + size + "},10);</script>";
    private static string RawCode(string raw)
    {
        using var output = new MemoryStream();
        using (var zip = new BrotliStream(output, CompressionLevel.Optimal,true)) zip.Write(Encoding.UTF8.GetBytes(raw));
        var bytes=output.ToArray();
        return "SCM1."+Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_')+"."+Convert.ToHexString(SHA256.HashData(bytes).AsSpan(0,4));
    }
    private static void Require(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
}
