using STS2SkinChanger.Core;

internal static class GitHubInstallLedgerTests
{
    public static void Run()
    {
        void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
        var install = new GitHubInstall("KaguyaSilentRavenSkin", "KaguyaSilentRavenSkin", "0.1.4");
        var ledger = new GitHubInstallLedger();
        // The card is named by the repository in every state, so the ledger must not keep the
        // name the package declares: a stored name is what used to rename the card on install.
        Require(typeof(GitHubInstall).GetProperties().Select(property => property.Name).OrderBy(name => name)
                .SequenceEqual(["Folder", "PackageId", "Version"]),
            "台账只留定位目录所需的字段，不保存任何显示名。");
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

        var path = Path.Combine(Path.GetTempPath(), "sc-gh-ledger-" + Guid.NewGuid().ToString("N"), "github_installs.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            ledger.Save(path, null);
            var restored = new GitHubInstallLedger();
            restored.Restore(path);
            Require(restored.Find("qbyoffice/SilentSkinMod") is { Folder: "KaguyaSilentRavenSkin", PackageId: "" },
                "安装归属必须跨会话恢复，缓存换格式也不能把已装的东西变成未安装。");

            Require(!restored.Remove("someone/else") && restored.Remove("qbyoffice/SilentSkinMod") && restored.Count == 0,
                "卸载只删除确实记录过的仓库。");

            File.WriteAllText(path, """{"Format":2,"Installs":{}}""");
            Require(Throws(() => restored.Restore(path)), "未知格式的台账必须拒绝，由调用方从空台账重新开始。");
            File.WriteAllText(path, """{"Format":1,"Installs":{"../evil":{"Folder":"a","PackageId":"a","PackageName":"","Version":""}}}""");
            restored.Restore(path);
            Require(restored.Count == 0, "缓存里的仓库名同样按不可信输入校验，非法条目直接丢弃。");
            File.WriteAllText(path, """{"Format":1,"Installs":{"o/r":{"Folder":"Mod","PackageId":"","PackageName":"","Version":"1.0"}}}""");
            restored.Restore(path);
            Require(restored.Find("o/r") is { Folder: "Mod" }, "合法的空清单条目必须能读回。");
            File.WriteAllText(path, "{");
            Require(Throws(() => restored.Restore(path)), "损坏的台账必须抛错，不能按半懂的数据继续跑。");
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
