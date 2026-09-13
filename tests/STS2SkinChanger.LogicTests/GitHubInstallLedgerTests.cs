using STS2SkinChanger.Core;

internal static class GitHubInstallLedgerTests
{
    private const string DigestA = "206ffa80a0b9f747c8a8fc3b909563158f6f9645fa7b39f029cc30b76e6c8c54";

    public static void Run()
    {
        void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
        var install = new GitHubInstall("KaguyaSilentRavenSkin", "KaguyaSilentRavenSkin", "0.1.4");
        var ledger = new GitHubInstallLedger();
        // The card is named by the repository in every state, so the ledger must not keep the
        // name the package declares: a stored name is what used to rename the card on install.
        Require(typeof(GitHubInstall).GetProperties().Select(property => property.Name).OrderBy(name => name)
                .SequenceEqual(["Digest", "Folder", "PackageId", "Version"]),
            "台账只留定位目录与判断更新所需的字段，不保存任何显示名。");
        Require(ledger.Record("qbyoffice/SilentSkinMod", install) && ledger.Count == 1,
            "安装成功必须记下仓库与目录的对应关系，卸载只认这条记录。");
        Require(ledger.Find("qbyoffice/silentskinmod") is { Folder: "KaguyaSilentRavenSkin", Version: "0.1.4" },
            "台账按仓库全名查找，大小写不敏感。");
        Require(ledger.Find("someone/else") == null, "没有记录的仓库必须是未安装，不能碰任何目录。");

        Require(!ledger.Record("../evil", install) && !ledger.Record("qbyoffice/SilentSkinMod", install with { Folder = "../evil" }) &&
            !ledger.Record("qbyoffice/SilentSkinMod", install with { PackageId = "a/b" }) &&
            !ledger.Record("qbyoffice/SilentSkinMod", install with { Version = new string('9', 65) }),
            "台账里的目录名与清单 ID 都会变成路径片段，非法值必须拒绝写入。");
        Require(ledger.Record("qbyoffice/SilentSkinMod", install with { PackageId = "" }),
            "没有清单的纯素材包也要能记录归属，否则它永远卸载不掉。");
        Require(ledger.Count == 1, "拒绝的记录不能留下半个条目。");
        // 校验值只是判断更新的依据：它不合法时丢掉这个值，安装归属照旧记下来。
        Require(ledger.Record("qbyoffice/SilentSkinMod", install with { PackageId = "", Digest = "md5:junk" }) &&
            ledger.Find("qbyoffice/SilentSkinMod") is { PackageId: "", Digest: "" } && ledger.Count == 1,
            "调用方给的校验值不合法时只丢这个值，不能连安装归属一起拒绝，否则装好了却记不下来。");

        var path = Path.Combine(Path.GetTempPath(), "sc-gh-ledger-" + Guid.NewGuid().ToString("N"), "github_installs.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            ledger.Save(path, null);
            var restored = new GitHubInstallLedger();
            restored.Restore(path);
            Require(restored.Find("qbyoffice/SilentSkinMod") is { Folder: "KaguyaSilentRavenSkin", PackageId: "" },
                "安装归属必须跨会话恢复，缓存换格式也不能把已装的东西变成未安装。");
            ledger.Record("qbyoffice/SilentSkinMod", install with { PackageId = "", Digest = DigestA });
            ledger.Save(path, null);
            restored.Restore(path);
            Require(restored.Find("qbyoffice/SilentSkinMod") is { Digest: DigestA },
                "安装时记下的包校验值必须跨会话读回，否则重启后没法判断远端有没有换包。");

            Require(!restored.Remove("someone/else") && restored.Remove("qbyoffice/SilentSkinMod") && restored.Count == 0,
                "卸载只删除确实记录过的仓库。");

            File.WriteAllText(path, """{"Format":3,"Installs":{}}""");
            Require(Throws(() => restored.Restore(path)), "未知格式的台账必须拒绝，由调用方从空台账重新开始。");
            File.WriteAllText(path, """{"Format":2,"Installs":{"../evil":{"Folder":"a","PackageId":"a","PackageName":"","Version":""}}}""");
            restored.Restore(path);
            Require(restored.Count == 0, "缓存里的仓库名同样按不可信输入校验，非法条目直接丢弃。");
            File.WriteAllText(path, """{"Format":2,"Installs":{"o/r":{"Folder":"Mod","PackageId":"","PackageName":"","Version":"1.0"}}}""");
            restored.Restore(path);
            Require(restored.Find("o/r") is { Folder: "Mod" }, "合法的空清单条目必须能读回。");
            // 升级前写下的台账没有校验值字段：它必须照旧读回，更新判断退回按版本号比较。
            File.WriteAllText(path, """{"Format":2,"Installs":{"o/r":{"Folder":"Mod","PackageId":"","Version":"1.0"}}}""");
            restored.Restore(path);
            Require(restored.Find("o/r") is { Folder: "Mod", Version: "1.0", Digest: "" },
                "旧版台账（没有校验值字段）必须原样读回，不能因为多了个字段就让已装的皮肤变成未安装。");
            File.WriteAllText(path, """{"Format":2,"Installs":{"o/r":{"Folder":"Mod","PackageId":"","Version":"1.0","Digest":"junk"}}}""");
            restored.Restore(path);
            Require(restored.Find("o/r") is { Folder: "Mod", Version: "1.0", Digest: "" },
                "文件里不认识的校验值只能丢掉这个值，不能连整份安装归属一起作废。");
            File.WriteAllText(path, "{");
            Require(Throws(() => restored.Restore(path)), "损坏的台账必须抛错，不能按半懂的数据继续跑。");

            // Steps that only the quit helper may run have to survive the same way: a session that
            // was closed by force still has to finish replacing or removing the folder next time.
            var staged = Path.Combine(Path.GetTempPath(), "sc-gh-ledger-staging");
            var queued = new GitHubInstallLedger();
            Require(queued.Record("o/r", install) &&
                queued.SetPending("o/r", new(GitHubModsOperationKind.Replace, install.Folder, staged)) &&
                queued.PendingFor("o/r") is { Kind: GitHubModsOperationKind.Replace, Folder: "KaguyaSilentRavenSkin", Staging: var staging } &&
                staging == staged,
                "游戏运行中无法替换的皮肤必须把替换步骤连同暂存目录一起记下来。");
            Require(queued.PendingFor("O/R") is not null, "待执行步骤也按仓库全名查找，大小写不敏感。");
            Require(!queued.SetPending("o/r", new(GitHubModsOperationKind.Replace, "../evil", staged)) &&
                !queued.SetPending("o/r", new(GitHubModsOperationKind.Replace, ".Skin.installing", staged)) &&
                !queued.SetPending("o/r", new(GitHubModsOperationKind.Replace, install.Folder, "relative\\path")) &&
                !queued.SetPending("o/r", new(GitHubModsOperationKind.Remove, install.Folder, staged)) &&
                !queued.SetPending("o/r", new((GitHubModsOperationKind)7, install.Folder, "")),
                "步骤里的目录名、暂存路径与步骤类型都会变成真实文件操作，非法值必须拒绝写入。");
            Require(queued.PendingFor("o/r") is { Kind: GitHubModsOperationKind.Replace },
                "被拒绝的步骤不能顶掉已经排好的那一条。");
            var kind = queued.SetPending("o/r", new(GitHubModsOperationKind.Remove, install.Folder, ""));
            Require(kind && queued.PendingFor("o/r") is { Kind: GitHubModsOperationKind.Remove, Staging: "" },
                "同一仓库只保留一个步骤：改排卸载必须顶掉还没执行的替换。");
            Require(queued.ClearPending("o/r") is { Kind: GitHubModsOperationKind.Remove } && queued.PendingFor("o/r") == null &&
                queued.ClearPending("o/r") == null,
                "步骤执行或作废后必须能从台账里清掉，并且只清一次。");

            queued.SetPending("o/r", new(GitHubModsOperationKind.Replace, install.Folder, staged));
            queued.Save(path, null);
            var resumed = new GitHubInstallLedger();
            resumed.Restore(path);
            Require(resumed.Find("o/r") is { Folder: "KaguyaSilentRavenSkin" } &&
                resumed.PendingFor("o/r") is { Kind: GitHubModsOperationKind.Replace, Staging: var resumedStaging } &&
                resumedStaging == staged,
                "待执行的替换步骤必须跨会话读回，否则强退之后那个目录再也换不掉。");
            File.WriteAllText(path, """{"Format":2,"Installs":{"o/r":{"Folder":"Mod","PackageId":"","Version":"1"}},"Pending":{"o/r":{"Kind":2,"Folder":"Other","Staging":"C:\\sc\\github-pending\\1"}}}""");
            resumed.Restore(path);
            Require(resumed.PendingFor("o/r") == null && resumed.Find("o/r") is { Folder: "Mod" },
                "未知的步骤类型只能丢弃这一条，不能连整份台账一起作废。");
            File.WriteAllText(path, """{"Format":2,"Installs":{"o/r":{"Folder":"Mod","PackageId":"","Version":"1"}},"Pending":{"o/r":{"Kind":1,"Folder":"Other","Staging":"C:\\sc\\github-pending\\1"}}}""");
            resumed.Restore(path);
            Require(resumed.PendingFor("o/r") == null,
                "替换步骤指向的目录与已记录的安装目录不一致时必须丢弃，否则退出助手会往别的目录里写包。");
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }

        Console.WriteLine("GitHub install ledger passed: ownership round-trip, invalid tokens and stale formats rejected.");
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (InvalidDataException) { return true; }
    }
}
