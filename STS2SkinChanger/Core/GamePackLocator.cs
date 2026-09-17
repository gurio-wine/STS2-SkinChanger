namespace STS2SkinChanger.Core;

internal static class GamePackLocator
{
    private const string CanonicalPackName = "SlayTheSpire2.pck";
    private const string MainPackArgumentName = "--main-pack";

    public static string Resolve(
        string executablePath,
        IReadOnlyList<string> commandLineArguments,
        Action<string>? warn = null)
    {
        var candidates = BuildCandidates(executablePath, commandLineArguments, warn);
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            "找不到游戏主资源包。已检查：" + string.Join("；", candidates));
    }

    internal static IReadOnlyList<string> BuildCandidates(
        string executablePath,
        IReadOnlyList<string> commandLineArguments,
        Action<string>? warn = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("游戏可执行文件路径为空。", nameof(executablePath));
        }

        var fullExecutablePath = Path.GetFullPath(executablePath);
        var executableDirectory = Path.GetDirectoryName(fullExecutablePath) ??
                                  throw new InvalidOperationException("无法取得游戏可执行文件目录。");
        var executableFileName = Path.GetFileName(fullExecutablePath);
        var executableBaseName = Path.GetFileNameWithoutExtension(fullExecutablePath);
        var directories = new List<string> { executableDirectory };

        // Godot places a separate main pack in Contents/Resources for macOS app bundles,
        // while OS.GetExecutablePath() points into Contents/MacOS. Windows and Linux keep
        // the pack next to the executable.
        if (Path.GetFileName(executableDirectory)
            .Equals("MacOS", StringComparison.OrdinalIgnoreCase))
        {
            directories.Insert(
                0,
                Path.GetFullPath(Path.Combine(executableDirectory, "..", "Resources")));
        }

        var packNames = new[]
            {
                CanonicalPackName,
                executableBaseName + ".pck",
                executableFileName + ".pck"
            }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var candidates = new List<string>();

        // The pack handed to the engine by the launcher outranks anything guessed from the
        // executable's location: it is the pack the engine was actually started with. Android
        // never keeps it next to the executable, so without this the mobile build finds nothing.
        var mainPackPath = MainPackPath(commandLineArguments);
        if (!string.IsNullOrWhiteSpace(mainPackPath))
        {
            try
            {
                candidates.Add(Path.GetFullPath(mainPackPath));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                warn?.Invoke($"启动参数 {MainPackArgumentName} 的路径无效，已忽略：" + exception.Message);
            }
        }

        foreach (var directory in directories.Distinct(PathComparer))
        {
            candidates.AddRange(packNames.Select(name => Path.Combine(directory, name)));

            // No pack is ever placed beside an executable that sits in a file system root, while
            // Android resolves the executable into "/" where the sandbox refuses to list. Only the
            // listing is skipped, so an install sitting directly at a drive root still resolves by
            // name; the remaining sources decide the result instead of failing the whole startup.
            if (IsFileSystemRoot(directory))
            {
                warn?.Invoke($"跳过文件系统根目录 {directory}，不在根目录枚举游戏资源包。");
                continue;
            }

            // macOS commonly uses a case-insensitive file system, but it is not guaranteed.
            // Match the known names ourselves so a case-sensitive volume also works.
            try
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    var fileName = Path.GetFileName(file);
                    if (packNames.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                    {
                        candidates.Add(file);
                    }
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                // One unreadable directory is a warning, never a failed start: the remaining
                // candidates, including the launcher's pack, still decide the result.
                warn?.Invoke($"无法枚举目录 {directory}，跳过该来源继续使用其它候选：" + exception.Message);
            }
        }

        return candidates
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .ToArray();
    }

    internal static bool IsFileSystemRoot(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var fullDirectory = Path.GetFullPath(directory);
        var root = Path.GetPathRoot(fullDirectory);
        return !string.IsNullOrEmpty(root) &&
               PathComparer.Equals(
                   Path.TrimEndingDirectorySeparator(fullDirectory),
                   Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
    }

    private static string? MainPackPath(IReadOnlyList<string>? commandLineArguments)
    {
        if (commandLineArguments == null)
        {
            return null;
        }

        for (var index = 0; index < commandLineArguments.Count; index++)
        {
            var argument = commandLineArguments[index];
            if (string.IsNullOrEmpty(argument))
            {
                continue;
            }

            if (argument.Equals(MainPackArgumentName, StringComparison.Ordinal))
            {
                // The pack is the next token; a trailing flag carries no path to use.
                return index + 1 < commandLineArguments.Count ? commandLineArguments[index + 1] : null;
            }

            if (argument.StartsWith(MainPackArgumentName + "=", StringComparison.Ordinal))
            {
                return argument[(MainPackArgumentName.Length + 1)..];
            }
        }

        return null;
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
