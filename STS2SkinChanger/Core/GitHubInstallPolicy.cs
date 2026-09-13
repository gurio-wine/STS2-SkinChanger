using System.Text.Json;

namespace STS2SkinChanger.Core;

internal enum GitHubInstallState { NotInstalled, Installed, Updatable }

internal static class GitHubInstallPolicy
{
    // Mirrors GamePackLocator: a macOS bundle keeps game data in Contents/Resources,
    // while OS.GetExecutablePath() points into Contents/MacOS.
    public static string ModsDirectory(string executablePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(executablePath))!;
        if (Path.GetFileName(directory).Equals("MacOS", StringComparison.OrdinalIgnoreCase))
            directory = Path.GetFullPath(Path.Combine(directory, "..", "Resources"));
        return Path.Combine(directory, "mods");
    }

    public static string FolderName(string packageId, string repoName)
    {
        // The manifest id is used verbatim or not at all: a "sanitized" id would name the
        // folder something the manifest inside it does not claim to be.
        if (IsSafeToken(packageId)) return packageId;
        var fallback = new string(repoName.Where(char.IsAsciiLetterOrDigit).ToArray());
        return fallback.Length is > 0 and <= 64 ? fallback : "Skin";
    }

    // Public because the install ledger stores these tokens and has to re-validate them.
    public static bool IsSafeToken(string? value) => value is { Length: > 0 and <= 64 } &&
        value[0] != '.' && value[^1] != '.' &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    public static int CompareVersions(string? left, string? right)
    {
        var a = Segments(left);
        var b = Segments(right);
        if (a.Length == 0 || b.Length == 0) return a.Length == b.Length ? 0 : a.Length == 0 ? -1 : 1;
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    private static int[] Segments(string? version) => (version ?? "").Trim().TrimStart('v', 'V')
        .Split('.', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => int.TryParse(new string(part.TakeWhile(char.IsAsciiDigit).ToArray()), out var value) ? value : -1).ToArray();

    public static GitHubInstallState Classify(string? installedVersion, string? availableVersion) =>
        string.IsNullOrWhiteSpace(installedVersion) ? GitHubInstallState.NotInstalled :
        !string.IsNullOrWhiteSpace(availableVersion) && CompareVersions(installedVersion, availableVersion) < 0
            ? GitHubInstallState.Updatable : GitHubInstallState.Installed;

    // Only a folder that still holds the manifest this mod installed may be removed: a folder
    // the player made by hand must never be deleted just because it shares the name. A package
    // without any manifest (a bundle-only card art pack) has no id to verify, and there the
    // ledger entry itself is the proof that this mod wrote the folder.
    public static string? UninstallTarget(string modsDirectory, string folder, string packageId)
    {
        if (!IsSafeToken(folder)) return null;
        var path = Path.Combine(modsDirectory, folder);
        if (!Directory.Exists(path)) return null;
        return packageId.Length == 0 || ReadManifestVersion(path, packageId) != null ? path : null;
    }

    public static string? ReadManifestVersion(string modRoot, string packageId)
    {
        if (!IsSafeToken(packageId)) return null;
        var path = Path.Combine(modRoot, packageId + ".json");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path).TrimStart('\uFEFF'));
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("version", out var version) &&
                version.ValueKind == JsonValueKind.String ? version.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }
}
