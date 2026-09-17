using System.Security.AccessControl;
using System.Security.Principal;
using STS2SkinChanger.Core;

internal static class GamePackLocatorTests
{
    public static void Run()
    {
        void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }

        var root = Path.Combine(Path.GetTempPath(), "sc-pack-locator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executableDirectory = Path.Combine(root, "install");
            Directory.CreateDirectory(executableDirectory);
            var executablePath = Path.Combine(executableDirectory, "SlayTheSpire2.exe");

            // 安卓上启动器用 --main-pack 把 PCK 注进来，它既不在可执行文件旁边，也不叫可执行文件的名字：
            // 只有认这个参数才能定位游戏资源包。
            var launcherDirectory = Path.Combine(root, "payloads", "sts2-v0.111.0-41cef1ea", "game");
            Directory.CreateDirectory(launcherDirectory);
            var launcherPack = Path.GetFullPath(Path.Combine(launcherDirectory, "SlayTheSpire2.pck"));
            File.WriteAllBytes(launcherPack, []);

            // 玩家日志里的真实参数形态：路径前面还有引擎自己的开关。
            var launcherArguments = new[]
            {
                "--rendering-method", "gl_compatibility", "--force-steam", "off", "--main-pack", launcherPack
            };
            var candidates = GamePackLocator.BuildCandidates(executablePath, launcherArguments);
            Require(candidates.Count > 0 && candidates[0] == launcherPack,
                "启动器注入的 --main-pack 必须排在候选首位：它才是引擎真正加载的资源包。");
            Require(GamePackLocator.Resolve(executablePath, launcherArguments) == launcherPack,
                "可执行文件旁边没有 PCK 时，必须靠 --main-pack 找到游戏资源包，否则安卓上初始化必然失败。");
            Require(GamePackLocator.Resolve(executablePath, ["--main-pack=" + launcherPack]) == launcherPack,
                "--main-pack=路径 这种写法同样要被接受，不能只认空格分隔。");
            Require(!GamePackLocator.BuildCandidates(executablePath, ["--main-pack"]).Contains(launcherPack),
                "--main-pack 后面没有值时不能凭空拿别的路径当资源包。");
            Require(!GamePackLocator.BuildCandidates(executablePath, ["--main-pack", "--force-steam"]).Contains(launcherPack),
                "--main-pack 后面跟的是另一个开关时不能把那个开关当成资源包路径。");

            // 桌面版不得回归：可执行文件旁边就能定位到 PCK 时，结果与改动前完全一致。
            var desktopPack = Path.GetFullPath(Path.Combine(executableDirectory, "SlayTheSpire2.pck"));
            File.WriteAllBytes(desktopPack, []);
            Require(GamePackLocator.Resolve(executablePath, []) == desktopPack,
                "桌面上没有 --main-pack 时定位结果必须与改动前一致，仍是可执行文件旁边的 PCK。");
            Require(GamePackLocator.Resolve(executablePath, ["--force-steam", "off", "-log", "Generic", "Debug"]) == desktopPack,
                "启动器的其它参数不能影响桌面定位结果。");
            Require(GamePackLocator.Resolve(executablePath, ["--main-pack", launcherPack, "--force-steam", "off"]) == launcherPack &&
                    GamePackLocator.BuildCandidates(executablePath, ["--main-pack", launcherPack])
                        .Count(candidate => candidate == launcherPack) == 1,
                "两处都有资源包时必须优先引擎实际加载的那个，并且只出现一次。");

            // 任何平台都不许枚举文件系统根目录：安卓上可执行文件目录就是「/」，沙箱拒绝列举。
            var fileSystemRoot = Path.GetPathRoot(Path.GetTempPath())!;
            Require(GamePackLocator.IsFileSystemRoot(fileSystemRoot) &&
                    GamePackLocator.IsFileSystemRoot("/") &&
                    GamePackLocator.IsFileSystemRoot(Path.Combine(fileSystemRoot, ".")),
                "文件系统根目录必须被识别出来，包括带尾分隔符和带 . 的写法。");
            Require(!GamePackLocator.IsFileSystemRoot(Path.GetTempPath()) &&
                    !GamePackLocator.IsFileSystemRoot(executableDirectory) &&
                    !GamePackLocator.IsFileSystemRoot(""),
                "普通目录、临时目录和空路径都不能被误判成根目录。");

            var rootWarnings = new List<string>();
            var rootCandidates = GamePackLocator.BuildCandidates(
                Path.Combine(fileSystemRoot, "SlayTheSpire2.exe"), [], rootWarnings.Add);
            Require(rootCandidates.Count == 2 &&
                    rootCandidates.Contains(Path.GetFullPath(Path.Combine(fileSystemRoot, "SlayTheSpire2.pck"))) &&
                    rootCandidates.Contains(Path.GetFullPath(Path.Combine(fileSystemRoot, "SlayTheSpire2.exe.pck"))),
                "可执行文件位于根目录时只能按名字拼候选，不得列举根目录里的文件。");
            Require(rootWarnings.Count == 1 && rootWarnings[0].Contains(fileSystemRoot),
                "跳过根目录必须留下恰好一条点名该目录的警告，否则玩家日志里看不出为什么没找到资源包。");

            // 目录不可访问只能降级成警告：这正是玩家日志里那次的 UnauthorizedAccessException。
            VerifyInaccessibleDirectoryDegrades(Require, root);

            Console.WriteLine(
                "Game pack locator passed: launcher --main-pack priority, file system root guard, " +
                "unreadable directory degradation and unchanged desktop resolution.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void VerifyInaccessibleDirectoryDegrades(Action<bool, string> require, string root)
    {
        var deniedDirectory = Path.Combine(root, "denied");
        Directory.CreateDirectory(deniedDirectory);
        File.WriteAllText(Path.Combine(deniedDirectory, "SlayTheSpire2.pck"), "");
        var executablePath = Path.Combine(deniedDirectory, "SlayTheSpire2.exe");
        var launcherPack = Path.GetFullPath(Path.Combine(root, "payloads", "game", "SlayTheSpire2.pck"));
        Directory.CreateDirectory(Path.GetDirectoryName(launcherPack)!);
        File.WriteAllBytes(launcherPack, []);

        // Windows 上不给管理员也照样能构造出「目录存在、但列举被拒」的状态，与安卓沙箱一致。
        // 造不出来（非 Windows、或权限不足）就跳过这一段，不让测试变成假绿。
        if (!TryDenyListing(deniedDirectory, out var restore))
        {
            Console.WriteLine("  (skipped unreadable-directory case: this host cannot deny directory listing)");
            return;
        }

        try
        {
            var listingIsDenied = false;
            try { _ = Directory.EnumerateFiles(deniedDirectory).ToArray(); }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException) { listingIsDenied = true; }
            require(listingIsDenied && Directory.Exists(deniedDirectory),
                "测试前提：目录必须仍然存在、但列举被拒绝，这才对应玩家日志里的 Permission denied。");
            if (!listingIsDenied)
            {
                return;
            }

            var warnings = new List<string>();
            var candidates = GamePackLocator.BuildCandidates(executablePath, [], warnings.Add);
            require(candidates.Contains(Path.GetFullPath(Path.Combine(deniedDirectory, "SlayTheSpire2.pck"))),
                "不可访问的目录仍要按名字给出候选，不能因为列举失败就丢掉这个来源。");
            require(warnings.Any(warning => warning.Contains(deniedDirectory)),
                "目录不可访问必须留下一条点名该目录的警告，而不是静默跳过。");

            // 玩家的实际场景：可执行文件目录不可列举，但启动器给了 --main-pack。
            require(GamePackLocator.Resolve(executablePath, ["--main-pack", launcherPack], warnings.Add) == launcherPack,
                "可执行文件目录不可访问时初始化不能整体失败，必须继续用 --main-pack 找到资源包。");

            // 列举被拒不代表按名字也读不到：目录里真有 PCK 时照样要能定位，且不能抛权限异常。
            require(GamePackLocator.Resolve(executablePath, []) ==
                    Path.GetFullPath(Path.Combine(deniedDirectory, "SlayTheSpire2.pck")),
                "不可列举的目录里就有 PCK 时仍必须按名字定位成功，列举失败不能连这个来源一起丢掉。");

            // 目录不可列举、里面又没有 PCK、也没有任何其它来源时，只能报「找不到资源包」。
            var emptyDenied = Path.Combine(root, "denied-empty");
            Directory.CreateDirectory(emptyDenied);
            if (TryDenyListing(emptyDenied, out var restoreEmpty))
            {
                try
                {
                    var reportedMissingPack = false;
                    var reported = "";
                    try { reported = "returned " + GamePackLocator.Resolve(Path.Combine(emptyDenied, "SlayTheSpire2.exe"), []); }
                    catch (UnauthorizedAccessException exception) { reported = "UnauthorizedAccessException: " + exception.Message; }
                    catch (FileNotFoundException) { reportedMissingPack = true; }
                    require(reportedMissingPack,
                        "所有来源都不可用且目录里没有 PCK 时只能报「找不到游戏主资源包」，" +
                        "不能让权限异常冒到初始化；实际：" + reported);
                }
                finally
                {
                    restoreEmpty();
                }
            }
        }
        finally
        {
            restore();
        }
    }

    private static bool TryDenyListing(string directory, out Action restore)
    {
        restore = () => { };
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            restore = DenyListing(directory);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or
                                              PlatformNotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }

    // The returned action is built here so its body is analysed under the same Windows-only
    // contract as the denial itself; the caller only has to invoke it.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Action DenyListing(string directory)
    {
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        var rule = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.ListDirectory,
            AccessControlType.Deny);
        security.AddAccessRule(rule);
        info.SetAccessControl(security);
        return () =>
        {
            var current = info.GetAccessControl();
            current.RemoveAccessRule(rule);
            info.SetAccessControl(current);
        };
    }
}
