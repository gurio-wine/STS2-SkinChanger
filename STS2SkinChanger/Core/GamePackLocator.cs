namespace STS2SkinChanger.Core;

internal static class GamePackLocator
{
    private const string CanonicalPackName = "SlayTheSpire2.pck";

    public static string Resolve(string executablePath)
    {
        var candidates = BuildCandidates(executablePath);
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

    internal static IReadOnlyList<string> BuildCandidates(string executablePath)
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
        foreach (var directory in directories.Distinct(PathComparer))
        {
            candidates.AddRange(packNames.Select(name => Path.Combine(directory, name)));

            // macOS commonly uses a case-insensitive file system, but it is not guaranteed.
            // Match the known names ourselves so a case-sensitive volume also works.
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

        return candidates
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .ToArray();
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
