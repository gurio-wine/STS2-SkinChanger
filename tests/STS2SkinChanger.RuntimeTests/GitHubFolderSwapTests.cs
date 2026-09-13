using STS2SkinChanger.Core;

// Installing, updating and removing a skin inside the game's mods folder. The rules come from the
// game's own loader, which this test mirrors instead of guessing: a directory is read as a mod only
// when it holds a file whose name ends in ".json" (ModManager.ReadModsInDirRecursive), and the DLL
// and the PCK are then taken from that manifest's own directory (ModManager.TryLoadMod).
//
// The running game is replaced by this process holding the same files open: a loaded skin's
// assembly and resource pack cannot be deleted or renamed on Windows, which is exactly what made
// the old build queue the work for a separate helper after the game had exited.
internal static class GitHubFolderSwapTests
{
    private const string DigestA = "206ffa80a0b9f747c8a8fc3b909563158f6f9645fa7b39f029cc30b76e6c8c54";

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "sc-github-swap-" + Guid.NewGuid().ToString("N"));
        var mods = Path.Combine(root, "mods");
        var package = Path.Combine(root, "package");
        var second = Path.Combine(root, "package-2");
        Directory.CreateDirectory(mods);
        WritePackage(package, "Skin", "new");
        WritePackage(second, "Skin", "newer");
        try
        {
            // ── 更新一个本次游戏已经加载的皮肤 ────────────────────────────────────────────────
            var live = Path.Combine(mods, "Skin");
            WritePackage(live, "Skin", "old");
            using var heldDll = Hold(Path.Combine(live, "Skin.dll"));
            using var heldPack = Hold(Path.Combine(live, "Skin.pck"));
            var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { live };
            bool IsLoaded(string path) => loaded.Contains(path);

            var placed = GitHubFolderSwap.Place(mods, "Skin", "Skin", "Skin", DigestA, package, IsLoaded, _ => { });
            Require(placed is { Folder: "Skin-206ffa80", Disabled: "Skin" },
                "被本次游戏加载的目录不能原地替换：新包必须写进旁边的备用目录，并报出被停用的是哪一个。");
            Require(File.ReadAllText(Path.Combine(mods, placed.Folder, "Skin.pck")) == "pack-new" &&
                File.Exists(Path.Combine(mods, placed.Folder, "Skin.json")),
                "备用目录里必须是刚下载的那份完整包。");
            Require(File.Exists(Path.Combine(live, "Skin.json.disabled")) && !File.Exists(Path.Combine(live, "Skin.json")),
                "旧目录的清单必须改名停用，否则下次启动会同时加载两份同 ID 的皮肤。");
            Require(!HoldsModManifest(live),
                "停用以后旧目录在游戏眼里必须已经不是 Mod——这是整个做法成立的前提。");
            Require(HoldsModManifest(Path.Combine(mods, placed.Folder)),
                "新目录必须带着能用的清单，否则这次更新白装。");
            Require(File.ReadAllText(Path.Combine(live, "Skin.pck")) == "pack-old" &&
                File.ReadAllText(Path.Combine(live, "Skin.dll")) == "assembly-old",
                "本次游戏正在用的文件一个都不能动，改名只碰清单。");

            // ── 玩家重启游戏后的那次刷新：旧目录终于可以整份删掉 ──────────────────────────────
            heldDll.Dispose(); heldPack.Dispose();
            var liveFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { placed.Folder };
            var remaining = GitHubFolderSwap.Sweep(mods, [("o/r", "Skin")], liveFolders, _ => { });
            Require(remaining.Length == 0 && !Directory.Exists(live) && Directory.Exists(Path.Combine(mods, placed.Folder)),
                "重启之后旧目录必须被清掉，而新的在用目录一个字节都不能碰。");

            // ── 没有加载时照旧原地替换，目录名不因为更新而变化 ────────────────────────────────
            var direct = GitHubFolderSwap.Place(mods, "Skin-206ffa80", "Skin", "Skin", DigestA, second, _ => false, _ => { });
            Require(direct is { Folder: "Skin-206ffa80", Disabled: null } &&
                File.ReadAllText(Path.Combine(mods, direct.Folder, "Skin.pck")) == "pack-newer" &&
                !File.Exists(Path.Combine(mods, direct.Folder, "Skin.json.disabled")),
                "没有任何进程占用时必须原地替换，不能给目录换个新名字。");

            // ── 第一次安装：直接写进清单里的那个目录名 ────────────────────────────────────────
            var fresh = GitHubFolderSwap.Place(mods, null, "NewSkin", "NewSkin", DigestA, PackageFor(root, "NewSkin"), _ => false, _ => { });
            Require(fresh is { Folder: "NewSkin", Disabled: null } && File.Exists(Path.Combine(mods, "NewSkin", "NewSkin.json")),
                "没有旧目录时安装就是写进正式目录名，不留备用目录。");

            // ── 台账丢了、但目录是本次游戏加载的：靠清单认领，仍然不能原地替换 ────────────────
            WritePackage(Path.Combine(mods, "Lost"), "Lost", "old");
            loaded.Add(Path.Combine(mods, "Lost"));
            using (var lostLock = Hold(Path.Combine(mods, "Lost", "Lost.dll")))
                Require(GitHubFolderSwap.Place(mods, null, "Lost", "Lost", DigestA, PackageFor(root, "Lost"), IsLoaded, _ => { }) is
                        { Folder: "Lost-206ffa80", Disabled: "Lost" },
                    "台账没有记录时，只凭目录里确实是这个皮肤的清单就该认出它是自己的，否则这个皮肤永远更新不了。");
            Require(!HoldsModManifest(Path.Combine(mods, "Lost")) && HoldsModManifest(Path.Combine(mods, "Lost-206ffa80")),
                "认领之后旧目录同样必须被停用。");

            // ── 别人的目录：名字撞上也不许停用，更不许写包 ────────────────────────────────────
            WritePackage(Path.Combine(mods, "Squatted"), "SomeoneElse", "old");
            loaded.Add(Path.Combine(mods, "Squatted"));
            var squattedPackage = PackageFor(root, "Squatted");
            using (var squattedLock = Hold(Path.Combine(mods, "Squatted", "SomeoneElse.dll")))
            {
                var rejected = false;
                try { GitHubFolderSwap.Place(mods, null, "Squatted", "Squatted", DigestA, squattedPackage, IsLoaded, _ => { }); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { rejected = true; }
                Require(rejected, "占着正式目录名、清单属于别的 Mod、又被加载的目录必须让安装失败，不能悄悄停用它。");
            }
            Require(HoldsModManifest(Path.Combine(mods, "Squatted")) && !Directory.Exists(Path.Combine(mods, "Squatted-206ffa80")),
                "被拒绝的安装不能留下任何改动：别人的目录和它的清单都要原样还在。");
            // 同样的目录没有被加载时，也不许被当成“可以覆盖的旧版本”。
            var occupied = false;
            try { GitHubFolderSwap.Place(mods, null, "Squatted", "Squatted", DigestA, squattedPackage, _ => false, _ => { }); }
            catch (InvalidOperationException) { occupied = true; }
            Require(occupied && File.Exists(Path.Combine(mods, "Squatted", "SomeoneElse.json")),
                "没有被加载、但清单属于别的 Mod 的目录同样不许覆盖，否则会把别人的皮肤删掉。");

            // ── 卸载后又装回来：目录里只剩自己改过名的清单，仍算自己的目录 ────────────────────
            var recycled = Path.Combine(mods, "Recycled");
            WritePackage(recycled, "Recycled", "old");
            File.Move(Path.Combine(recycled, "Recycled.json"), Path.Combine(recycled, "Recycled.json.disabled"));
            loaded.Add(recycled);
            using (var recycledLock = Hold(Path.Combine(recycled, "Recycled.dll")))
                Require(GitHubFolderSwap.Place(mods, null, "Recycled", "Recycled", DigestA, PackageFor(root, "Recycled"), IsLoaded, _ => { }) is
                        { Folder: "Recycled-206ffa80", Disabled: "Recycled" },
                    "卸载过一次的目录里只有本 Mod 改过名的清单，重新安装必须照样能换掉它。");

            // ── 删除的三道保险 ────────────────────────────────────────────────────────────────
            var inUse = Path.Combine(mods, "InUse");
            WritePackage(inUse, "InUse", "old");
            File.Move(Path.Combine(inUse, "InUse.json"), Path.Combine(inUse, "InUse.json.disabled"));
            var reinstalled = Path.Combine(mods, "Reinstalled");
            WritePackage(reinstalled, "Reinstalled", "new");
            var swept = GitHubFolderSwap.Sweep(mods,
                [("o/r", "InUse"), ("o/r", "Reinstalled"), ("o/r", "GoneAlready")],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "InUse" }, _ => { });
            Require(swept.Length == 0, "已经作废的等待项（在用目录、重新装了清单的目录、已经不存在的目录）不能留在列表里反复重试。");
            Require(Directory.Exists(inUse), "台账正指着的在用目录绝不能删，哪怕它的清单已经被停用。");
            Require(File.Exists(Path.Combine(reinstalled, "Reinstalled.json")),
                "重新出现活清单的目录绝不能删：那不是本 Mod 停用的那一个了。");

            if (OperatingSystem.IsWindows())
            {
                var lockedPending = Path.Combine(mods, "LockedPending");
                WritePackage(lockedPending, "LockedPending", "old");
                File.Move(Path.Combine(lockedPending, "LockedPending.json"), Path.Combine(lockedPending, "LockedPending.json.disabled"));
                using var stillHeld = Hold(Path.Combine(lockedPending, "LockedPending.dll"));
                var retry = GitHubFolderSwap.Sweep(mods, [("o/r", "LockedPending")], new HashSet<string>(StringComparer.OrdinalIgnoreCase), _ => { });
                Require(retry.SequenceEqual(["LockedPending"]) && Directory.Exists(lockedPending),
                    "还被进程占着的旧目录必须留在等待列表里，下次启动继续删，而不是被静默忘掉。");
            }
            Console.WriteLine("GitHub folder swap passed: hide-and-replace, real locked files, sweep guards and legacy recovery.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // A package as an author publishes it: a manifest plus the two files the game loads from it.
    private static void WritePackage(string directory, string id, string tag)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, id + ".json"), Manifest(id));
        File.WriteAllText(Path.Combine(directory, id + ".dll"), "assembly-" + tag);
        File.WriteAllText(Path.Combine(directory, id + ".pck"), "pack-" + tag);
    }

    private static string PackageFor(string root, string id)
    {
        var directory = Path.Combine(root, "package-" + id);
        WritePackage(directory, id, "downloaded");
        return directory;
    }

    private static string Manifest(string id) =>
        $$"""{"id":"{{id}}","name":"{{id}}","version":"1.0","hasDll":true,"hasPck":true}""";

    // What the running game holds open: on Windows an open handle is what blocks the delete and the
    // rename, so this is the same obstacle, not a stand-in for it.
    private static FileStream Hold(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    // The loader's rule, spelled out: a directory holds a mod when a file whose name ends in
    // ".json" can be read as a manifest with an id (ReadModsInDirRecursive + ReadModManifest).
    private static bool HoldsModManifest(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any(file =>
            Path.GetFileName(file).EndsWith(".json", StringComparison.Ordinal) && File.ReadAllText(file).Contains("\"id\""));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
