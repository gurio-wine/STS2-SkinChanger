using STS2SkinChanger.Core;

internal static class GitHubInfoPolicyTests
{
    public static void Run()
    {
        void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
        var id = SkinSourceIdentity.GitHubId("qbyoffice", "SilentSkinMod");
        var item = new WorkshopCatalogItem(id, [new("character", "silent")], true);

        // sc.info 就是作者把扫描结果粘进去的文件：说明文字、Markdown 代码块都得照读不误。
        var published = "# SilentSkinMod\n\n信息码：\n\n```\n" + string.Join("\n", WorkshopSubmissionCodec.Encode(item, "1.0.8.15", "0.107.1")) + "\n```\n";
        var read = GitHubInfoPolicy.Read(published, id);
        Require(read.Error.Length == 0 && read.Item is { } recognized &&
            recognized.Targets.Single() is { Kind: "character", Target: "silent" } && recognized.RestartRequired,
            "sc.info 里的信息码必须能被读回，得到替换对象与重启标记。");

        Require(GitHubInfoPolicy.Read(null, id) is { Item: null, Error: "missing" },
            "没有 sc.info 要明确报 missing，而不是猜成已识别。");
        Require(GitHubInfoPolicy.Read("   ", id) is { Item: null, Error: "no-code" } &&
            GitHubInfoPolicy.Read("这是说明，没有任何码。", id) is { Item: null, Error: "no-code" },
            "只有说明文字的文件不算已识别。");

        var other = SkinSourceIdentity.GitHubId("someone", "OtherSkin");
        Require(GitHubInfoPolicy.Read(string.Join("\n", WorkshopSubmissionCodec.Encode(item with { Id = other }, "1", "0.107.1")), id)
            is { Item: null, Error: "id-mismatch" },
            "别的仓库的信息码不能被安到本仓库头上，否则标签与包根本不是一回事。");

        var single = WorkshopSubmissionCodec.Encode(item, "1", "0.107.1").Single();
        var broken = single[..^1] + (single[^1] == '0' ? '1' : '0');
        Require(GitHubInfoPolicy.Read(broken, id) is { Item: null, Error: "checksum" },
            "校验位坏掉的信息码必须拒绝，不能静默当成已识别。");
        Require(GitHubInfoPolicy.Read("SCM99 abc 1/1 body.12345678", id) is { Item: null, Error: "version" },
            "未知版本的信息码必须拒绝。");

        // 一个包里几百张卡是很正常的事：信息码会被分成多段，读回时必须按组拼回去。
        var huge = new WorkshopCatalogItem(id, Enumerable.Range(0, 300)
            .Select(index => new WorkshopTarget("cards", "card_" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(index))))).ToArray(), true);
        var split = WorkshopSubmissionCodec.Encode(huge, "1", "0.107.1");
        Require(split.Length > 1 && GitHubInfoPolicy.Read(string.Join("\n", split), id).Item is { Targets.Length: 300 },
            "分段的信息码必须按组拼回，长目标列表不能只读回一段。");

        Require(GitHubInfoPolicy.Read(new string('x', GitHubInfoPolicy.MaxBytes + 1), id) is { Item: null, Error: "too-large" },
            "超过上限的 sc.info 必须直接拒绝，不能把它读进内存再解析。");
        Console.WriteLine("GitHub info policy passed: sc.info codes read back, missing/mismatch/broken rejected.");
    }
}
